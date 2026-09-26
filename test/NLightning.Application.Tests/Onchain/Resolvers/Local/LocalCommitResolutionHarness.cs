using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Tests.Onchain.Resolvers.Local;

using Application.Onchain.Resolvers;
using Application.Onchain.Resolvers.Local;
using Channels.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Payments.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Onchain;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// A fake chain around <see cref="LocalCommitResolver"/>: Alice's (our) real commitment of a
/// <see cref="RealSigningCommitmentPair"/> confirmed at <see cref="CloseHeight"/>, in-memory output rows, watches and
/// broadcast rows, and a stand-in for the on-chain resolution executor that applies the resolver's actions the way the
/// <c>IOutputResolver</c> contract describes (rows, broadcasts to a mempool, watches, staged writes, then events and
/// alerts), marks a spent output <c>Resolved</c> and calls <c>OnOutputSpentAsync</c> before <c>ResolveAsync</c> on
/// every mined block.
/// </summary>
internal sealed class LocalCommitResolutionHarness : IDisposable
{
    public const uint CloseHeight = 1_000;
    public const uint FeeratePerKw = 1_000;

    /// <summary>The peer's <c>to_self_delay</c>: the CSV on our <c>to_local</c> and HTLC transaction outputs.</summary>
    public const ushort Csv = 100;

    private static readonly Hash s_blockHash = new(new byte[32]);

    private readonly ServiceProvider _provider;
    private readonly Dictionary<uint256, Transaction> _knownTransactions = [];
    private readonly Dictionary<uint, List<Transaction>> _blocks = [];
    private readonly HashSet<OutPoint> _spentOnChain = [];

    public RealSigningCommitmentPair Pair { get; }
    public ChannelModel Channel => Pair.Alice.Channel;
    public LocalCommitResolver Resolver { get; }
    public ChannelCloseModel Close { get; }
    public Transaction CommitmentTransaction { get; }
    public uint Height { get; private set; } = CloseHeight;
    public byte[] Destination { get; } = new Key(Enumerable.Repeat((byte)0x42, 32).ToArray()).PubKey.WitHash
                                                                                             .ScriptPubKey.ToBytes();

    public Dictionary<(TxId, uint), OutputResolutionModel> Rows { get; } = [];
    public Dictionary<(TxId, uint), WatchedOutpointModel> Watches { get; } = [];
    public Dictionary<TxId, BroadcastTransactionModel> Broadcasts { get; } = [];
    public List<Transaction> Mempool { get; } = [];
    public List<(uint Height, IChannelDomainEvent Event)> Events { get; } = [];
    public List<AlertAction> Alerts { get; } = [];

    /// <summary>The sweep fee estimate the fee service answers (sat/kw).</summary>
    public uint FeeEstimatePerKw { get; set; } = FeeratePerKw;

    /// <summary>False: a mined spend of a watched output is recorded, but <c>OnOutputSpentAsync</c> is not called (its
    /// staged writes lost, as when the round's save failed).</summary>
    public bool NotifySpends { get; set; } = true;

    /// <summary>True: mined blocks leave the mempool out (a fee too low to be mined, O6-T1 tests).</summary>
    public bool HoldMempool { get; set; }

    /// <summary>False: <c>IBitcoinChainService.GetTransactionAsync</c> finds nothing (no txindex, RPC down).</summary>
    public bool ChainServiceFindsTransactions { get; set; } = true;

    /// <summary>False: <c>IBitcoinChainService.GetBlockAsync(height)</c> finds nothing (pruned node, RPC down).</summary>
    public bool ChainServiceFindsBlocks { get; set; } = true;

    /// <summary>True: <c>IBitcoinChainService.GetBlockAsync(height)</c> throws, as the real service does on every RPC
    /// error (pruned block, height past the tip).</summary>
    public bool ChainServiceThrowsOnBlocks { get; set; }

    /// <summary>True: every round's save fails: nothing after the save (events, alerts) happens.</summary>
    public bool SavesFail { get; set; }

    /// <summary>False: mined blocks skip the per-block <c>ResolveAsync</c> round (the node is offline).</summary>
    public bool ResolveEachBlock { get; set; } = true;

    /// <summary>What the executor did, in order ("stage ...", "raise ...", "broadcast ...").</summary>
    public List<string> Log { get; } = [];

    /// <summary>Every snapshot staged through <c>IChannelStateDbRepository.ApplyAsync</c>.</summary>
    public List<ChannelTransition> Applied { get; } = [];

    /// <summary>Forwards of an incoming HTLC, as <c>FindHtlcsByOriginAsync</c> answers them.</summary>
    public Dictionary<HtlcOrigin, List<(ChannelId, HtlcKey)>> Forwards { get; } = [];

    /// <summary>Our invoices by payment hash (the final-hop decision asks the switch only for an <c>Open</c> one).</summary>
    public Dictionary<Hash, InvoiceModel> Invoices { get; } = [];

    /// <param name="setup">Moves the pair to the state whose commitment goes on chain.</param>
    /// <param name="hasAnchors">An option_anchors channel (O7-T3).</param>
    /// <param name="feeInputProvider">The wallet's fee inputs for anchors HTLC transactions (the default registration
    /// when null).</param>
    /// <param name="wrapSigner">Replaces Alice's signer in the resolver's container (e.g. with a decorator).</param>
    /// <param name="resolverLogger">The resolver's logger (a null logger when null).</param>
    public LocalCommitResolutionHarness(Action<RealSigningCommitmentPair>? setup = null, bool hasAnchors = false,
                                        IAnchorFeeInputProvider? feeInputProvider = null,
                                        Func<ILightningSigner, ILightningSigner>? wrapSigner = null,
                                        ILogger<LocalCommitResolver>? resolverLogger = null)
    {
        Pair = new RealSigningCommitmentPair(hasAnchors);
        setup?.Invoke(Pair);
        Channel.UpdateCommitments(Pair.Alice.State);

        var feeService = new Mock<IFeeService>();
        feeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(() => LightningMoney.Satoshis(FeeEstimatePerKw));
        feeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(() => LightningMoney.Satoshis(FeeEstimatePerKw));
        var chainService = new Mock<IBitcoinChainService>();
        chainService.Setup(c => c.GetTransactionAsync(It.IsAny<uint256>()))
                    .ReturnsAsync((uint256 txId) => ChainServiceFindsTransactions
                                                        ? _knownTransactions.GetValueOrDefault(txId)
                                                        : null);
        chainService.Setup(c => c.GetBlockAsync(It.IsAny<uint>()))
                    .ReturnsAsync((uint height) => ChainServiceThrowsOnBlocks
                                                       ? throw new InvalidOperationException("Block not available")
                                                       : ChainServiceFindsBlocks && _blocks.TryGetValue(height, out var txs)
                                                           ? BuildBlock(txs)
                                                           : null);
        // gettxout without the mempool: an output of a known (confirmed) transaction that no mined one spends
        chainService.Setup(c => c.GetConfirmedUnspentOutputAsync(It.IsAny<OutPoint>()))
                    .ReturnsAsync((OutPoint outPoint) =>
                                      _knownTransactions.TryGetValue(outPoint.Hash, out var parent)
                                   && outPoint.N < parent.Outputs.Count && !_spentOnChain.Contains(outPoint)
                                          ? (parent.Outputs[(int)outPoint.N], CloseHeight)
                                          : ((TxOut Output, uint Height)?)null);
        var destinations = new Mock<ISweepDestinationProvider>();
        destinations.Setup(d => d.GetDestinationScriptAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Destination);

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(new NodeOptions()));
        services.AddSingleton(new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IUtxoMemoryRepository>().Object);
        services.AddBitcoinInfrastructure();
        services.AddSingleton(chainService.Object);
        services.AddSingleton(wrapSigner is null ? Pair.Alice.Signer : wrapSigner(Pair.Alice.Signer));
        if (feeInputProvider is not null)
            services.AddSingleton(feeInputProvider);
        services.AddSingleton<ICommitmentTransactionModelFactory, CommitmentTransactionModelFactory>();
        services.AddOnchainBitcoinServices();
        services.AddSingleton(feeService.Object);
        services.AddSingleton(destinations.Object);
        services.AddScoped(_ => CreateUnitOfWork().Object);
        if (resolverLogger is not null)
            services.AddSingleton(resolverLogger);
        services.AddLocalCommitResolutionServices();
        _provider = services.BuildServiceProvider();
        Resolver = _provider.GetRequiredService<LocalCommitResolver>();

        var model = _provider.GetRequiredService<ICommitmentTransactionModelFactory>()
                             .CreateCommitmentTransactionModel(Channel,
                                                               CommitmentTxSpec.FromCommitmentSpec(
                                                                   Pair.Alice.State.LocalCommit.Spec),
                                                               CommitmentSide.Local,
                                                               Pair.Alice.State.LocalCommit.Number);
        var built = _provider.GetRequiredService<ICommitmentTransactionBuilder>().BuildWithOutputMap(model);
        CommitmentTransaction = Transaction.Load(built.Transaction.RawTxBytes, Network.Main);
        _knownTransactions[CommitmentTransaction.GetHash()] = CommitmentTransaction;
        Close = new ChannelCloseModel(Channel.ChannelId, ChannelCloseKind.LocalCommitment, built.Transaction.TxId,
                                      Pair.Alice.State.LocalCommit.Number, CloseHeight, s_blockHash,
                                      DateTimeOffset.UtcNow);
    }

    public TxId CommitmentTxId => Close.CommitmentTransactionId;

    /// <summary>Makes <paramref name="transaction"/> known to the chain (e.g. the wallet transaction a fee input
    /// spends), so <see cref="AssertAllInputsVerify"/> finds the outputs it holds.</summary>
    public void AddKnownTransaction(Transaction transaction) => _knownTransactions[transaction.GetHash()] = transaction;

    /// <summary>The executor's round right after classification (or after a restart).</summary>
    public async Task<IReadOnlyList<OutputResolverAction>> ResolveAsync()
    {
        var actions = await Resolver.ResolveAsync(Close, Rows.Values.ToList(), Height,
                                                  TestContext.Current.CancellationToken);
        await ApplyAsync(actions);
        return actions;
    }

    /// <summary>
    /// Mines one block holding the mempool and <paramref name="transactions"/>: every watched outpoint they spend is
    /// marked spent, its row <c>Resolved</c> and <c>OnOutputSpentAsync</c> is called; rows 100 deep become
    /// <c>Irrevocable</c>; then <c>ResolveAsync</c> runs at the new tip.
    /// </summary>
    public async Task MineAsync(params Transaction[] transactions)
    {
        Height++;
        var block = (HoldMempool ? [] : Mempool).Concat(transactions).ToList();
        if (!HoldMempool)
            Mempool.Clear();
        foreach (var tx in transactions)
            Mempool.RemoveAll(m => m.GetHash() == tx.GetHash());
        _blocks[Height] = block;
        foreach (var tx in block)
        {
            _knownTransactions[tx.GetHash()] = tx;
            foreach (var input in tx.Inputs)
                _spentOnChain.Add(input.PrevOut);
            var txId = new TxId(tx.GetHash().ToBytes());
            if (Broadcasts.TryGetValue(txId, out var broadcast))
                broadcast.MarkConfirmed(Height, s_blockHash);

            foreach (var input in tx.Inputs)
            {
                var key = (new TxId(input.PrevOut.Hash.ToBytes()), input.PrevOut.N);
                if (!Watches.TryGetValue(key, out var watch) || watch.IsSpent)
                    continue;

                watch.MarkSpent(txId, Height, s_blockHash);
                var row = Rows[key] with { State = OutputResolutionState.Resolved, ResolvedHeight = Height };
                Rows[key] = row;
                if (NotifySpends)
                    await ApplyAsync(await Resolver.OnOutputSpentAsync(Close, row,
                                                                       ChainTxMapper.FromTransaction(tx), Height,
                                                                       TestContext.Current.CancellationToken));
            }
        }

        foreach (var (key, row) in Rows.ToList())
        {
            if (row is { State: OutputResolutionState.Resolved, ResolvedHeight: { } resolved }
             && Height - resolved + 1 >= 100)
                Rows[key] = row with { State = OutputResolutionState.Irrevocable };
        }

        if (ResolveEachBlock)
            await ResolveAsync();
    }

    private static Block BuildBlock(IEnumerable<Transaction> transactions)
    {
        var block = Network.Main.Consensus.ConsensusFactory.CreateBlock();
        block.Transactions.AddRange(transactions);
        return block;
    }

    /// <summary>Mines empty blocks (plus the mempool) until the tip is <paramref name="height"/>.</summary>
    public async Task MineToAsync(uint height)
    {
        while (Height < height)
            await MineAsync();
    }

    /// <summary>The row of an output of our commitment.</summary>
    public OutputResolutionModel CommitmentRow(uint vout) => Rows[(CommitmentTxId, vout)];

    /// <summary>The vout of the commitment output paying <paramref name="kind"/> (the first one).</summary>
    public uint VoutOf(OutputDescriptorKind kind) =>
        Rows.Values.First(r => r.TransactionId == CommitmentTxId && r.Descriptor == kind).OutputIndex;

    /// <summary>The transactions broadcast for <paramref name="purpose"/>, in order.</summary>
    public IReadOnlyList<Transaction> Broadcast(BroadcastPurpose purpose) =>
        Broadcasts.Values.Where(b => b.Purpose == purpose)
                  .OrderBy(b => b.CreatedAt)
                  .Select(b => Transaction.Load(b.RawTransaction, Network.Main))
                  .ToList();

    /// <summary>Runs every input's witness against the output it spends.</summary>
    public void AssertAllInputsVerify(Transaction tx)
    {
        for (var i = 0; i < tx.Inputs.Count; i++)
        {
            var prevOut = tx.Inputs[i].PrevOut;
            var spent = _knownTransactions.TryGetValue(prevOut.Hash, out var parent)
                            ? parent.Outputs[prevOut.N]
                            : Broadcasts.Values.Select(b => Transaction.Load(b.RawTransaction, Network.Main))
                                        .First(t => t.GetHash() == prevOut.Hash).Outputs[prevOut.N];
            Assert.True(tx.Inputs.AsIndexedInputs().ElementAt(i).VerifyScript(spent, out var error),
                        $"input {i} of {tx.GetHash()}: {error}");
        }
    }

    public void Dispose()
    {
        _provider.Dispose();
        Pair.Dispose();
    }

    /// <summary>A unit of work over the harness's rows (for the sweep scheduler).</summary>
    public IUnitOfWork CreateUnitOfWorkForTests() => CreateUnitOfWork().Object;

    /// <summary>Applies actions as the executor would (rows, broadcasts to the mempool, stages, then events).</summary>
    public Task ApplyActionsAsync(IReadOnlyList<OutputResolverAction> actions) => ApplyAsync(actions);

    /// <summary>A service of the harness's container (the signer, the fee service).</summary>
    public T GetService<T>() where T : notnull => _provider.GetRequiredService<T>();

    private async Task ApplyAsync(IReadOnlyList<OutputResolverAction> actions)
    {
        var unitOfWork = CreateUnitOfWork().Object;
        foreach (var action in actions)
        {
            switch (action)
            {
                case UpsertOutputAction upsert:
                    Rows[(upsert.Output.TransactionId, upsert.Output.OutputIndex)] = upsert.Output;
                    break;
                case BroadcastAction broadcast when !Broadcasts.ContainsKey(broadcast.Transaction.TransactionId):
                    Broadcasts[broadcast.Transaction.TransactionId] = broadcast.Transaction;
                    if (broadcast.Transaction.ReplacesTransactionId is { } replaced)
                        Mempool.RemoveAll(m => new TxId(m.GetHash().ToBytes()) == replaced); // BIP 125
                    Mempool.Add(Transaction.Load(broadcast.Transaction.RawTransaction, Network.Main));
                    Log.Add($"broadcast {broadcast.Transaction.Purpose}");
                    break;
                case WatchOutpointAction watch:
                    Watches.TryAdd((watch.Watch.TransactionId, watch.Watch.OutputIndex), watch.Watch);
                    break;
                case StageWriteAction stage:
                    await stage.Stage(unitOfWork, TestContext.Current.CancellationToken);
                    Log.Add($"stage {stage.Description}");
                    break;
            }
        }

        // After the save: events and alerts
        if (SavesFail)
            return;

        foreach (var action in actions)
        {
            switch (action)
            {
                case RaiseChannelEventAction raise:
                    Events.Add((Height, raise.Event));
                    Log.Add($"raise {raise.Event.GetType().Name}");
                    break;
                case AlertAction alert:
                    Alerts.Add(alert);
                    alert.Emitted?.Invoke();
                    break;
            }
        }
    }

    private Mock<IUnitOfWork> CreateUnitOfWork()
    {
        var channels = new Mock<IChannelDbRepository>();
        channels.Setup(r => r.GetByIdAsync(It.IsAny<ChannelId>()))
                .ReturnsAsync((ChannelId id) => id == Channel.ChannelId ? Channel : null);

        var channelState = new Mock<IChannelStateDbRepository>();
        channelState.Setup(r => r.ApplyAsync(It.IsAny<ChannelCommitments>(), It.IsAny<ChannelTransition>(),
                                             It.IsAny<ChannelStateExtras?>()))
                    .Callback((ChannelCommitments next, ChannelTransition transition, ChannelStateExtras? _) =>
                     {
                         // The database (and the model reloaded from it) now holds the staged snapshot
                         Applied.Add(transition);
                         Channel.UpdateCommitments(next);
                     })
                    .Returns(Task.CompletedTask);
        channelState.Setup(r => r.FindHtlcsByOriginAsync(It.IsAny<HtlcOrigin>()))
                    .ReturnsAsync((HtlcOrigin origin) => Forwards.TryGetValue(origin, out var found)
                                                             ? found
                                                             : []);

        var watches = new Mock<IWatchedOutpointDbRepository>();
        watches.Setup(r => r.GetAsync(It.IsAny<TxId>(), It.IsAny<uint>()))
               .ReturnsAsync((TxId txId, uint vout) => Watches.GetValueOrDefault((txId, vout)));

        var broadcasts = new Mock<IBroadcastTransactionDbRepository>();
        broadcasts.Setup(r => r.GetByTransactionIdAsync(It.IsAny<TxId>()))
                  .ReturnsAsync((TxId txId) => Broadcasts.GetValueOrDefault(txId));
        broadcasts.Setup(r => r.GetByChannelIdAsync(It.IsAny<ChannelId>()))
                  .ReturnsAsync(() => Broadcasts.Values.OrderBy(b => b.CreatedAt).ToList());
        broadcasts.Setup(r => r.MarkReplacedAsync(It.IsAny<TxId>()))
                  .ReturnsAsync((TxId txId) =>
                   {
                       if (Broadcasts.GetValueOrDefault(txId) is not { State: BroadcastState.Pending } broadcast)
                           return false;
                       broadcast.MarkReplaced();
                       return true;
                   });
        broadcasts.Setup(r => r.MarkAbandonedAsync(It.IsAny<TxId>()))
                  .ReturnsAsync((TxId txId) =>
                   {
                       if (Broadcasts.GetValueOrDefault(txId) is not { State: BroadcastState.Pending } broadcast)
                           return false;
                       broadcast.MarkAbandoned();
                       Mempool.RemoveAll(m => new TxId(m.GetHash().ToBytes()) == txId);
                       return true;
                   });

        var resolutions = new Mock<IOnchainResolutionDbRepository>();
        resolutions.Setup(r => r.GetOutputsByChannelIdAsync(It.IsAny<ChannelId>()))
                   .ReturnsAsync(() => Rows.Values.ToList());

        var circuits = new Mock<IForwardCircuitDbRepository>();
        circuits.Setup(r => r.GetByIncomingAsync(It.IsAny<ChannelId>(), It.IsAny<ulong>()))
                .ReturnsAsync((ForwardCircuitModel?)null);
        var invoices = new Mock<IInvoiceDbRepository>();
        invoices.Setup(r => r.GetByPaymentHashAsync(It.IsAny<Hash>()))
                .ReturnsAsync((Hash hash) => Invoices.GetValueOrDefault(hash));

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.ForwardCircuitDbRepository).Returns(circuits.Object);
        unitOfWork.SetupGet(u => u.InvoiceDbRepository).Returns(invoices.Object);
        unitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(channels.Object);
        unitOfWork.SetupGet(u => u.ChannelStateDbRepository).Returns(channelState.Object);
        unitOfWork.SetupGet(u => u.WatchedOutpointDbRepository).Returns(watches.Object);
        unitOfWork.SetupGet(u => u.BroadcastTransactionDbRepository).Returns(broadcasts.Object);
        unitOfWork.SetupGet(u => u.OnchainResolutionDbRepository).Returns(resolutions.Object);
        return unitOfWork;
    }
}