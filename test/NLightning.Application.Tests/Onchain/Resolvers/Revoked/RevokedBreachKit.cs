using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Tests.Onchain.Resolvers.Revoked;

using Application.Onchain.Resolvers;
using Application.Onchain.Resolvers.Revoked;
using Channels.Services;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Onchain;
using Infrastructure.Bitcoin.Services;

/// <summary>
/// A breach on a fake chain with real crypto: two commitment engines with real signers
/// (<see cref="RealSigningCommitmentPair"/>; Alice is the cheater, Bob the victim), the revoked commitment <c>k</c>
/// Alice could broadcast (rebuilt byte for byte as Bob signed it), her HTLC-timeout/success transactions for it, and a
/// driver that plays the on-chain resolution executor's part for <see cref="RevokedCommitResolver"/>: it applies the
/// actions (rows, broadcasts, watches, switch events, alerts), confirms transactions and reports the spends of
/// watched outputs.
/// </summary>
internal sealed class RevokedBreachKit : IDisposable
{
    public const uint SpentAtHeight = 500;
    public const uint FeeratePerKw = 2_500;

    public static readonly byte[] Destination =
        new Key(Enumerable.Repeat((byte)0x55, 32).ToArray()).PubKey.WitHash.ScriptPubKey.ToBytes();

    private readonly RevokedCommitResolverOptions? _options;
    private readonly ServiceProvider _services;

    public RealSigningCommitmentPair Pair { get; }
    public RealSigningNode Cheater => Pair.Alice;
    public RealSigningNode Victim => Pair.Bob;

    public ulong RevokedNumber { get; private set; }
    public CommitmentSpec RevokedSpec { get; private set; } = null!;
    public CommitmentSpec CheaterLocalSpec { get; private set; } = null!;
    public CompactPubKey RevokedPoint { get; private set; }
    public Transaction RevokedCommitment { get; private set; } = null!;
    public ChainTx RevokedChainTx { get; private set; } = null!;

    public IKeyDerivationService KeyDerivationService { get; }
    public FakeRevokedCommitDataSource DataSource { get; } = new();
    public RevokedCommitResolver Resolver { get; private set; }

    public List<OutputResolutionModel> Rows { get; } = [];
    public Dictionary<TxId, BroadcastTransactionModel> Broadcasts { get; } = [];
    public HashSet<(TxId, uint)> Watches { get; } = [];
    public List<IChannelDomainEvent> Events { get; } = [];
    public List<AlertAction> Alerts { get; } = [];
    public Dictionary<TxId, uint> Confirmed { get; } = [];

    public ChannelCloseModel Close => new(RealSigningCommitmentPair.ChannelId, ChannelCloseKind.RevokedCommitment,
                                          RevokedChainTx.TxId, RevokedNumber, SpentAtHeight,
                                          new Hash(new byte[32]), DateTimeOffset.UtcNow);

    public RevokedBreachKit(RevokedCommitResolverOptions? options = null)
    {
        Pair = new RealSigningCommitmentPair(false);

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(new NodeOptions()));
        services.AddBitcoinInfrastructure();
        _services = services.BuildServiceProvider();
        KeyDerivationService = _services.GetRequiredService<IKeyDerivationService>();

        _options = options;
        Resolver = CreateResolver();
        DataSource.Kit = this;
    }

    /// <summary>A new resolver instance, as after a restart of the node: nothing it kept in memory survives.</summary>
    public void RestartResolver() => Resolver = CreateResolver();

    private RevokedCommitResolver CreateResolver()
    {
        var sweepBuilder = new SweepTransactionBuilder(Options.Create(new NodeOptions()));
        return new RevokedCommitResolver(DataSource, CreateMapper(Victim), new PenaltyTransactionBuilder(sweepBuilder),
                                         sweepBuilder, Victim.Signer, KeyDerivationService,
                                         NullLogger<RevokedCommitResolver>.Instance,
                                         _options is null ? null : Options.Create(_options));
    }

    /// <summary>
    /// Captures the cheater's current commitment as the one it will broadcast (Bob's <c>RemoteCommit</c> = Alice's
    /// <c>LocalCommit</c>).
    /// </summary>
    public void CaptureRevokedState()
    {
        Assert.Equal(Cheater.State.LocalCommit.Number, Victim.State.RemoteCommit.Number);
        RevokedNumber = Victim.State.RemoteCommit.Number;
        RevokedSpec = Victim.State.RemoteCommit.Spec;
        CheaterLocalSpec = Cheater.State.LocalCommit.Spec;
        RevokedPoint = Victim.State.RemoteCommit.PerCommitmentPoint;

        var factory = CreateModelFactory(Victim);
        var model = factory.CreateCommitmentTransactionModel(Victim.Channel,
                                                             CommitmentTxSpec.FromCommitmentSpec(RevokedSpec),
                                                             CommitmentSide.Remote, RevokedNumber, RevokedPoint);
        var built = new CommitmentTransactionBuilder(Options.Create(new NodeOptions())).BuildWithOutputMap(model);
        RevokedCommitment = Transaction.Load(built.Transaction.RawTxBytes, Network.Main);
        RevokedChainTx = ChainTxMapper.FromTransaction(RevokedCommitment);
    }

    /// <summary>
    /// After the cheater revoked <see cref="RevokedNumber"/>: the victim's channel carries its current snapshot and the
    /// data source serves the breach (the secret from the cheater's signer, as the victim's shachain would).
    /// </summary>
    public void Breach(RevokedCommitmentModel? logEntry = null, ulong logStart = 0, bool useLog = true)
    {
        Assert.True(Victim.State.RemoteCommit.Number > RevokedNumber, "The cheater has not revoked the state yet");
        Victim.Channel.UpdateCommitments(Victim.State);

        var secret = Cheater.Signer.RevealPerCommitmentSecret(RealSigningCommitmentPair.ChannelId, RevokedNumber);
        var entry = useLog
                        ? logEntry ?? (RevokedSpec.Htlcs.Count > 0
                                           ? new RevokedCommitmentModel(RealSigningCommitmentPair.ChannelId,
                                                                        RevokedNumber, RevokedSpec)
                                           : null)
                        : null;
        DataSource.Context = new RevokedCommitContext(Victim.Channel, RevokedChainTx, RevokedNumber, secret,
                                                      RevokedPoint, entry, logStart);
        Confirm(RevokedChainTx, SpentAtHeight, false);
    }

    /// <summary>One round of the executor: resolve, then apply.</summary>
    public async Task<IReadOnlyList<OutputResolverAction>> RunAsync(uint height)
    {
        var actions = await Resolver.ResolveAsync(Close, Rows.ToList(), height, TestContext.Current.CancellationToken);
        Apply(actions);
        return actions;
    }

    /// <summary>
    /// A block at <paramref name="height"/> holds <paramref name="transaction"/>: every watched output it spends is
    /// marked Resolved and the resolver is told (as the executor does).
    /// </summary>
    public async Task<IReadOnlyList<OutputResolverAction>> ConfirmAsync(ChainTx transaction, uint height)
    {
        Confirm(transaction, height, true);
        var all = new List<OutputResolverAction>();
        foreach (var input in transaction.Inputs)
        {
            var index = Rows.FindIndex(r => r.TransactionId == input.PreviousTxId
                                         && r.OutputIndex == input.PreviousVout);
            if (index < 0)
                continue;

            Rows[index] = Rows[index] with { State = OutputResolutionState.Resolved, ResolvedHeight = height };
            var actions = await Resolver.OnOutputSpentAsync(Close, Rows[index], transaction, height,
                                                             TestContext.Current.CancellationToken);
            Apply(actions);
            all.AddRange(actions);
        }

        return all;
    }

    public void Apply(IEnumerable<OutputResolverAction> actions)
    {
        foreach (var action in actions)
        {
            switch (action)
            {
                case UpsertOutputAction upsert:
                    var index = Rows.FindIndex(r => r.TransactionId == upsert.Output.TransactionId
                                                 && r.OutputIndex == upsert.Output.OutputIndex);
                    if (index < 0)
                        Rows.Add(upsert.Output);
                    else
                        Rows[index] = upsert.Output;
                    break;
                case BroadcastAction broadcast:
                    Broadcasts.TryAdd(broadcast.Transaction.TransactionId, broadcast.Transaction);
                    break;
                case WatchOutpointAction watch:
                    Watches.Add((watch.Watch.TransactionId, watch.Watch.OutputIndex));
                    break;
                case RaiseChannelEventAction raise:
                    Events.Add(raise.Event);
                    break;
                case AlertAction alert:
                    Alerts.Add(alert);
                    break;
            }
        }
    }

    /// <summary>The spends recorded for watched outputs (the chain monitor's rows).</summary>
    public Dictionary<(TxId, uint), (ChainTx Transaction, uint Height)> Spends { get; } = [];

    private void Confirm(ChainTx transaction, uint height, bool recordSpends)
    {
        Confirmed[transaction.TxId] = height;
        if (!recordSpends)
            return;

        foreach (var input in transaction.Inputs)
            Spends[(input.PreviousTxId, input.PreviousVout)] = (transaction, height);
    }

    /// <summary>The cheater's HTLC-timeout (its offered HTLC) or HTLC-success (with the preimage) for an HTLC output of
    /// the revoked commitment, with a witness of the right shape (only the outputs matter to the victim).</summary>
    public ChainTx CheaterSecondLevel(HtlcDirection victimDirection, ulong htlcId, Secret? preimage = null)
    {
        var mapper = CreateMapper(Cheater);
        var cheaterDirection = victimDirection == HtlcDirection.Outgoing ? HtlcDirection.Incoming
                                                                          : HtlcDirection.Outgoing;
        var map = mapper.Map(Cheater.Channel, CommitmentTxSpec.FromCommitmentSpec(CheaterLocalSpec),
                             CommitmentCase.Local, RevokedNumber, null, RevokedChainTx);
        Assert.True(map.TxIdMatched);
        var descriptor = map.Outputs.Single(o => o.Htlc is { } h && h.Direction == cheaterDirection
                                               && h.Id == htlcId);
        var built = new HtlcTransactionBuilder(Options.Create(new NodeOptions())).Build(descriptor.SecondLevel!);
        var tx = ChainTxMapper.FromTransaction(Transaction.Load(built.Transaction.RawTxBytes, Network.Main));

        var signature = Enumerable.Repeat((byte)0x30, 71).ToArray();
        byte[] last = preimage is { } p ? p : [];
        var input = tx.Inputs[0];
        var witness = new List<byte[]> { Array.Empty<byte>(), signature, signature, last, descriptor.WitnessScript! };
        return tx with { Inputs = [input with { Witness = witness }] };
    }

    /// <summary>Our broadcast transactions that are not replaced or confirmed yet, in broadcast order.</summary>
    public Transaction LoadBroadcast(TxId txId) => Transaction.Load(Broadcasts[txId].RawTransaction, Network.Main);

    /// <summary>The spent output of an input of one of our transactions (commitment or cheater second level).</summary>
    public TxOut SpentOutput(OutPoint outPoint, params ChainTx[] candidates)
    {
        foreach (var candidate in candidates.Append(RevokedChainTx))
        {
            if (new uint256((byte[])candidate.TxId) == outPoint.Hash)
            {
                var output = candidate.Outputs[(int)outPoint.N];
                return new TxOut(Money.Satoshis(output.AmountSat), new Script(output.ScriptPubKey));
            }
        }

        throw new InvalidOperationException($"Unknown outpoint {outPoint}");
    }

    /// <summary>Runs every input of <paramref name="txId"/> against the output it spends.</summary>
    public void AssertVerifies(TxId txId, params ChainTx[] spentFrom)
    {
        var tx = LoadBroadcast(txId);
        var indexed = tx.Inputs.AsIndexedInputs().ToList();
        foreach (var input in indexed)
        {
            var spent = SpentOutput(input.PrevOut, spentFrom);
            Assert.True(input.VerifyScript(spent, out var error), $"input {input.Index}: {error}");
        }
    }

    public static ChainTx ToChainTx(Transaction tx) => ChainTxMapper.FromTransaction(tx);

    private CommitmentOutputMapper CreateMapper(RealSigningNode node) =>
        new(CreateModelFactory(node), new CommitmentTransactionBuilder(Options.Create(new NodeOptions())));

    private CommitmentTransactionModelFactory CreateModelFactory(RealSigningNode node) =>
        new(new CommitmentKeyDerivationService(KeyDerivationService, node.Signer), node.Signer);

    public void Dispose()
    {
        _services.Dispose();
        Pair.Dispose();
    }
}

/// <summary>
/// <see cref="IRevokedCommitDataSource"/> over <see cref="RevokedBreachKit"/>'s fake chain.
/// </summary>
internal sealed class FakeRevokedCommitDataSource : IRevokedCommitDataSource
{
    public RevokedBreachKit Kit { get; set; } = null!;
    public RevokedCommitContext? Context { get; set; }
    public uint Feerate { get; set; } = RevokedBreachKit.FeeratePerKw;
    public int LoadCount { get; private set; }

    /// <summary>When true, recorded spends come back without their transaction (a fetch that failed).</summary>
    public bool FetchFails { get; set; }

    /// <summary>When true, stored broadcasts cannot be read back (their fee is unknown).</summary>
    public bool BroadcastsHidden { get; set; }

    public Task<RevokedCommitLoadResult> LoadAsync(ChannelCloseModel close, CancellationToken cancellationToken)
    {
        LoadCount++;
        return Task.FromResult(Context is { } context
                                   ? RevokedCommitLoadResult.Found(context)
                                   : RevokedCommitLoadResult.Missing("no context"));
    }

    public Task<RevokedOutputSpend?> GetSpendAsync(TxId transactionId, uint outputIndex,
                                                   CancellationToken cancellationToken)
    {
        if (!Kit.Spends.TryGetValue((transactionId, outputIndex), out var spend))
            return Task.FromResult<RevokedOutputSpend?>(null);

        return Task.FromResult<RevokedOutputSpend?>(
            new RevokedOutputSpend(spend.Transaction.TxId, FetchFails ? null : spend.Transaction, spend.Height,
                                   Kit.Broadcasts.ContainsKey(spend.Transaction.TxId)));
    }

    public Task<bool> IsOurTransactionAsync(TxId transactionId) =>
        Task.FromResult(Kit.Broadcasts.ContainsKey(transactionId));

    public Task<BroadcastTransactionModel?> GetBroadcastAsync(TxId transactionId) =>
        Task.FromResult(BroadcastsHidden ? null : Kit.Broadcasts.GetValueOrDefault(transactionId));

    public Task<byte[]> GetDestinationScriptAsync(Domain.Channels.ValueObjects.ChannelId channelId,
                                                  CancellationToken cancellationToken) =>
        Task.FromResult(RevokedBreachKit.Destination);

    public Task<uint> GetFeeratePerKwAsync(CancellationToken cancellationToken) => Task.FromResult(Feerate);
}