using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Tests.Channels.Splicing;

using Application.Channels.Handlers;
using Application.Channels.Handlers.Interfaces;
using Application.Channels.Quiescence;
using Application.Channels.Splicing;
using Application.Channels.Splicing.Exceptions;
using Application.Channels.Splicing.Handlers;
using Application.Channels.Splicing.Interfaces;
using Application.InteractiveTx;
using Application.InteractiveTx.Interfaces;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.InteractiveTx.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Harness;
using Infrastructure.Bitcoin.InteractiveTx;
using InteractiveTx.TestDoubles;

/// <summary>
/// Two <see cref="TwoNodeHarness"/> nodes with the production splice (<c>AddSpliceServices</c>), quiescence and
/// interactive-tx services, every channel message (<c>stfu</c>, <c>tx_*</c>, <c>splice_*</c> and the splice
/// <c>commitment_signed</c>) delivered through the receiver's <c>ChannelManager</c>, the real Appendix G transaction
/// builder, and stand-ins for what lanes SP1-B/SP1-C provide: <see cref="FakeSpliceStatePort"/> (fundings, splice
/// commitment signatures) and <see cref="SpliceSigningProxy"/> (funding key rotation, <c>shared_input_signature</c> with
/// invariant SP-I1). Wallet inputs come from <see cref="FakeInteractiveTxContributor"/>.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class SpliceHarness : IDisposable
{
    public const uint FeeratePerKw = 1_000;

    private readonly Dictionary<string, SpliceNode> _nodes = [];
    private readonly Action<string, NodeOptions>? _configureNode;

    public TwoNodeHarness Harness { get; }

    /// <summary>Whether the nodes run on the production engine port and signer (see the constructor).</summary>
    public bool RealEngine { get; }

    /// <summary>Every message either node raised, in raise (wire) order: (sender, message).</summary>
    public ConcurrentQueue<(string From, IChannelMessage Message)> Transcript { get; } = new();

    /// <summary>Exceptions the receivers' channel managers threw for a delivered message: (receiver, exception).</summary>
    public ConcurrentQueue<(string Node, Exception Exception)> Failures { get; } = new();

    public SpliceNode Alice => _nodes["Alice"];
    public SpliceNode Bob => _nodes["Bob"];

    /// <param name="configureSplice">Per-node splice options.</param>
    /// <param name="realEngine">Run on lanes SP1-B/SP1-C's real code instead of the stand-ins: the production
    /// <see cref="EngineSpliceStatePort"/> over the several-funding engine, the real <c>LocalLightningSigner</c>
    /// splice members (funding key rotation, SP-I1, the 2-of-2 shared input) and an in-memory
    /// <see cref="InMemoryChannelFundingRepository"/> committed with each save.</param>
    /// <param name="announceChannel">A public channel (<see cref="TwoNodeHarness"/>'s <c>announceChannel</c>; lane SP2-B).</param>
    /// <param name="configureServices">Adds or replaces services of each node after the splice services (last
    /// registration wins; lane SP2-B).</param>
    /// <param name="configureNode">Per-node node options, e.g. the liquidity ads rates a node sells at (NL-771; the
    /// other node sees them as its peer's <c>init</c> rates).</param>
    public SpliceHarness(Action<string, SpliceOptions>? configureSplice = null, bool realEngine = false,
                         bool announceChannel = false,
                         Action<HarnessNode, IServiceCollection>? configureServices = null,
                         Action<string, NodeOptions>? configureNode = null)
    {
        RealEngine = realEngine;
        _configureNode = configureNode;
        Harness = new TwoNodeHarness(announceChannel: announceChannel,
                                     configureServices: (node, services) =>
                                     {
                                         Configure(node, services, configureSplice);
                                         configureServices?.Invoke(node, services);
                                     });
        foreach (var node in new[] { Harness.Alice, Harness.Bob })
            AttachNode(_nodes[node.Name], node);
        Harness.BeforeRestore = RestoreLockedFunding;
    }

    /// <summary>
    /// As the channel row after a splice lock (<c>IChannelFundingDbRepository.ApplyLockAsync</c> moves its funding
    /// columns, key index and short channel id; NL-496): a restarted node whose saved snapshot runs on a locked splice
    /// gets that funding on its channel model, so the restore registers the signer with it.
    /// </summary>
    private void RestoreLockedFunding(HarnessNode node, ChannelModel channel)
    {
        if (!RealEngine || node.Store.Committed?.Params.Funding is not { } current
                        || current.FundingTxId == channel.FundingOutput?.TransactionId)
            return;

        var locked = _nodes[node.Name].FundingRows.Committed.GetValueOrDefault(current.FundingTxId) ?? current;
        channel.ReplaceFundingOutput(new FundingOutputInfo(LightningMoney.Satoshis(locked.CapacitySatoshis),
                                                           locked.LocalFundingPubKey, locked.RemoteFundingPubKey,
                                                           locked.FundingTxId, locked.OutputIndex));
        channel.SetLocalFundingKeyIndex(locked.LocalFundingKeyIndex);
        if (locked.ShortChannelId is { } shortChannelId)
            channel.ShortChannelId = shortChannelId;
    }

    /// <summary>
    /// Stops <paramref name="node"/> and starts a new process on what it saved (<see cref="TwoNodeHarness.RestartNodeAsync"/>,
    /// link down): the interactive-tx driver, the splice service and the state port start empty, the node's
    /// <see cref="SpliceNode.Sessions"/> and <see cref="SpliceNode.FundingRows"/> keep only their committed rows.
    /// <see cref="TwoNodeHarness.ReconnectAsync"/> brings the link back.
    /// </summary>
    public async Task RestartAsync(SpliceNode node)
    {
        node.Sessions.DiscardStaged();
        node.FundingRows.DiscardStaged();
        node.Purchases.DiscardStaged();
        RestorePendingFundingsFromRows(node);
        var restarted = await Harness.RestartNodeAsync(node.Node);
        AttachNode(node, restarted);
        RegisterSavedFundings(node);
    }

    /// <summary>
    /// As <c>ChannelStateDbRepository.LoadAsync</c> (NL-496): the pending fundings of the restored snapshot come from
    /// their saved <c>ChannelFundings</c> rows, which alone carry what the lock steps write (<c>splice_locked</c> sent
    /// and received, the confirmation height, the short channel id). The in-memory state store keeps the snapshot as the
    /// engine last applied it, so the rows' records replace its pending fundings before the restart reads it.
    /// </summary>
    private void RestorePendingFundingsFromRows(SpliceNode node)
    {
        var store = node.Node.Store;
        if (!RealEngine || store.Committed is not { PendingFundings.IsEmpty: false } committed)
            return;

        var rows = node.FundingRows.Committed;
        var pending = committed.PendingFundings
                               .Select(f => rows.TryGetValue(f.FundingTxId, out var row)
                                         && row.Status == ChannelFundingStatus.Pending
                                                ? row
                                                : f)
                               .ToImmutableList();

        // PendingFundings has a private init accessor: set it on a copy of the record
        var copy = (ChannelCommitments)typeof(ChannelCommitments).GetMethod("<Clone>$")!.Invoke(committed, null)!;
        typeof(ChannelCommitments).GetProperty(nameof(ChannelCommitments.PendingFundings))!.SetValue(copy, pending);
        store.Seed(copy);
    }

    /// <summary>
    /// NL-508: registers the fundings the restarted node saved the way production loads them at the first signature
    /// through <c>ChannelSigningInfoDbRepository</c> (the restart registered the channel through
    /// <c>channel.GetSigningInfo</c>, which carries none): every saved funding row except the current one, and, while
    /// the restored local commitment carries the peer's signatures, every pending funding's number as its SP-I1 mark.
    /// <c>ILightningSigner.RegisterChannel</c> takes both (<c>RestoreSpliceState</c>).
    /// </summary>
    private static void RegisterSavedFundings(SpliceNode node)
    {
        var channel = node.Node.Channel;
        var current = channel.Commitments?.Params.Funding?.FundingTxId ?? channel.FundingOutput!.TransactionId!.Value;
        var others = node.FundingRows.Committed.Values.Where(f => f.FundingTxId != current).ToList();
        if (others.Count == 0)
            return;

        // As ChannelSigningInfoDbRepository: a pending funding whose local commitment slot carries the peer's
        // signatures (the slot ReceiveSpliceCommitmentAsync saved) is marked at that slot's number; the current
        // funding's signatures do not matter (the first commitment of a channel has none, NL-496)
        Dictionary<TxId, ulong>? persisted = null;
        var commitments = channel.Commitments;
        if (commitments is { PendingFundings.Count: > 0 })
        {
            persisted = commitments.PendingFundings
                                   .Where(f => commitments.LocalCommit.SignaturesFor(f.FundingTxId) is not null)
                                   .ToDictionary(f => f.FundingTxId, _ => commitments.LocalCommit.Number);
            if (persisted.Count == 0)
                persisted = null;
        }

        node.Node.Signer.RegisterChannel(channel.ChannelId, channel.GetSigningInfo() with
        {
            Fundings = others,
            PersistedSpliceCommitments = persisted
        });
    }

    private void AttachNode(SpliceNode spliceNode, HarnessNode node)
    {
        spliceNode.Attach(node);
        node.NegotiatedFeatures = CreateFeatures();
        var name = node.Name;
        node.ChannelManager.OnResponseMessageReady += (_, args) => Transcript.Enqueue((name, args.ResponseMessage));
    }

    public static FeatureOptions CreateFeatures() => new()
    {
        AllowExperimentalFeatures = true,
        OptionQuiesce = FeatureSupport.Optional,
        OptionSplice = FeatureSupport.Optional
    };

    /// <summary>The node id a harness node gets from its seed (see <c>HarnessNode</c>).</summary>
    public static CompactPubKey NodeIdOf(string name) =>
        new Key(Enumerable.Repeat(name == "Alice" ? (byte)0xA1 : (byte)0xB0, 32).ToArray()).PubKey.ToBytes();

    public SpliceNode Other(SpliceNode node) => ReferenceEquals(node, Alice) ? Bob : Alice;

    /// <summary>The transcript's message types in order, with their sender (for sequence assertions).</summary>
    public List<string> Sequence(Func<IChannelMessage, bool>? filter = null) =>
        Transcript.Where(t => filter?.Invoke(t.Message) ?? true)
                  .Select(t => $"{t.From}:{Enum.GetName(t.Message.Type)}")
                  .ToList();

    /// <summary>Delivers messages both ways until nothing moves and <paramref name="until"/> (if any) completed.</summary>
    public async Task PumpAsync(Task? until = null)
    {
        for (var round = 0; round < 2_000; round++)
        {
            await WhenIdleAsync();
            var aliceSent = await DeliverAsync(Harness.Alice);
            var bobSent = await DeliverAsync(Harness.Bob);
            if (aliceSent || bobSent)
                continue;

            await WhenIdleAsync();
            if (!Harness.Alice.OutboxIsEmpty || !Harness.Bob.OutboxIsEmpty)
                continue;

            if (until is null || until.IsCompleted)
                return;

            // A background continuation (the splice_init after the quiescence) may still publish
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException("The splice exchange did not converge");
    }

    /// <summary>Starts a splice on <paramref name="initiator"/> and pumps until it completes.</summary>
    public async Task<Domain.Channels.Splicing.Models.SpliceResult> SpliceAsync(SpliceNode initiator,
                                                                                 long contributionSatoshis)
    {
        var request = new Domain.Channels.Splicing.Models.SpliceRequest(TwoNodeHarness.ChannelId,
                                                                         contributionSatoshis, FeeratePerKw);
        var start = initiator.Service.StartAsync(request, TestContext.Current.CancellationToken);
        await PumpAsync(start);
        return await start;
    }

    /// <summary>
    /// The chain monitors of both nodes report the splice transaction at its required depth, in the given order
    /// (a node reaches the depth when its monitor says so); pumps after each.
    /// </summary>
    public async Task ConfirmAsync(TxId spliceTxId, uint height, params SpliceNode[] nodes)
    {
        foreach (var node in nodes)
        {
            node.Confirm(spliceTxId, height);
            await node.DepthWatcher.WhenIdleAsync();
            await PumpAsync();
        }
    }

    public async Task WhenIdleAsync()
    {
        for (var round = 0; round < 3; round++)
        {
            foreach (var node in new[] { Harness.Alice, Harness.Bob })
            {
                await node.Scheduler.WhenIdleAsync();
                await node.Services.GetRequiredService<QuiescenceService>().WhenIdleAsync();
                await node.Services.GetRequiredService<SpliceService>().WhenIdleAsync();
                await node.Services.GetRequiredService<SpliceDepthWatcher>().WhenIdleAsync();
            }
        }
    }

    public void Dispose() => Harness.Dispose();

    private async Task<bool> DeliverAsync(HarnessNode from)
    {
        try
        {
            if (from.PeekNext() is not StartBatchMessage startBatch)
                return await from.DeliverNextAsync();

            // As the peer's inbound loop groups them (lane SP1-A): start_batch and its commitment_signed messages are
            // handed to the channel manager as one batch, under one acquisition of the channel's lock
            from.TryTakeNext(out _);
            var members = new List<CommitmentSignedMessage>();
            for (var i = 0; i < startBatch.Payload.BatchSize; i++)
            {
                if (!from.TryTakeNext(out var member))
                    throw new InvalidOperationException("A start_batch was published without all its members");
                members.Add((CommitmentSignedMessage)member);
            }

            await from.Peer.ChannelManager.HandleCommitmentSignedBatchAsync(
                new CommitmentSignedBatch(startBatch.Payload.ChannelId, members), from.NegotiatedFeatures, from.NodeId);
            return true;
        }
        catch (Exception e) when (e is WarningException or ChannelErrorException)
        {
            Failures.Enqueue((from.Peer.Name, e));
            return true;
        }
    }

    private void Configure(HarnessNode node, IServiceCollection services, Action<string, SpliceOptions>? configure)
    {
        // A restarted node keeps its stores (what it saved)
        if (!_nodes.TryGetValue(node.Name, out var spliceNode))
        {
            spliceNode = new SpliceNode(node.Name);
            _nodes[node.Name] = spliceNode;
        }

        var options = new NodeOptions { EnableHtlcs = true };
        options.Features.AllowExperimentalFeatures = true;
        options.Features.OptionQuiesce = FeatureSupport.Optional;
        options.Features.OptionSplice = FeatureSupport.Optional;
        _configureNode?.Invoke(node.Name, options);
        spliceNode.Options = options;
        services.AddSingleton(Options.Create(options));
        services.Configure<SpliceOptions>(o => configure?.Invoke(node.Name, o));

        // Liquidity ads (NL-771): the other node's rates are this node's peer's init rates
        var peerName = node.Name == "Alice" ? "Bob" : "Alice";
        SpliceLiquidityTestKit.AddLiquidityAds(
            services, () => _nodes.GetValueOrDefault(peerName)?.Options?.LiquidityAds.GetWillFundRates());

        // SP1-C's signer members: the proxy over the harness's real signer, unless the real ones run
        if (!RealEngine)
        {
            var descriptor = services.Last(d => d.ServiceType == typeof(ILightningSigner));
            services.Add(ServiceDescriptor.Singleton<ILightningSigner>(sp =>
                             SpliceSigningProxy.Create((ILightningSigner)descriptor.ImplementationFactory!(sp),
                                                       spliceNode)));
        }

        services.AddQuiescenceServices();
        services.AddSingleton<IInteractiveTxBuilder, InteractiveTxBuilder>();
        services.AddSingleton<IPrevTxInspector>(spliceNode.Inspector);
        services.AddSingleton<IInteractiveTxContributor>(spliceNode.Contributor);
        services.AddInteractiveTxServices();
        if (!RealEngine)
            services.AddSingleton<ISpliceStatePort>(sp =>
                                                        spliceNode.CreatePort(
                                                            sp.GetRequiredService<ILightningSigner>()));
        services.AddSingleton<ISpliceOutDestination>(spliceNode.Destination);
        services.AddSpliceServices();
        var fundingSpends = new Mock<Application.Onchain.Interfaces.IOnchainChannelWatcher>();
        fundingSpends.Setup(w => w.HandleFundingSpentAsync(It.IsAny<OutpointSpentEventArgs>(),
                                                           It.IsAny<CancellationToken>()))
                     .Callback<OutpointSpentEventArgs, CancellationToken>((args, _) =>
                                                                             spliceNode.FundingSpends.Enqueue(args))
                     .ReturnsAsync((Application.Onchain.Interfaces.FundingSpendOutcome?)null);
        services.AddSingleton(fundingSpends.Object);

        services.AddScoped<IChannelMessageHandler<StfuMessage>, StfuMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxAddInputMessage>, TxAddInputMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxAddOutputMessage>, TxAddOutputMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxRemoveInputMessage>, TxRemoveInputMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxRemoveOutputMessage>, TxRemoveOutputMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxCompleteMessage>, TxCompleteMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxSignaturesMessage>, TxSignaturesMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxInitRbfMessage>, TxInitRbfMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxAckRbfMessage>, TxAckRbfMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxAbortMessage>, TxAbortMessageHandler>();
        services.AddScoped<IChannelMessageHandler<SpliceInitMessage>, SpliceInitMessageHandler>();
        services.AddScoped<IChannelMessageHandler<SpliceAckMessage>, SpliceAckMessageHandler>();
        services.AddScoped<IChannelMessageHandler<SpliceLockedMessage>, SpliceLockedMessageHandler>();
    }
}

/// <summary>One side of <see cref="SpliceHarness"/>: the stand-ins and records of that node.</summary>
[ExcludeFromCodeCoverage]
internal sealed class SpliceNode(string name)
{
    private HarnessNode? _node;

    public string Name { get; } = name;
    public HarnessNode Node => _node ?? throw new InvalidOperationException("Not attached");
    public FakeInteractiveTxContributor Contributor { get; } = new();
    public FakePrevTxInspector Inspector { get; } = new();
    public FakeSpliceOutDestination Destination { get; } = new();
    public InMemoryInteractiveTxSessionRepository Sessions { get; } = new();
    public FakeSpliceStatePort Port { get; private set; } = null!;

    /// <summary>The <c>ChannelFundings</c> rows of the node (real-engine mode; staged, then committed by a save).</summary>
    public InMemoryChannelFundingRepository FundingRows { get; } = new();

    /// <summary>The <c>LiquidityPurchases</c> rows of the node (NL-771; staged, then committed by a save).</summary>
    public InMemorySpliceLiquidityPurchases Purchases { get; } = new();

    /// <summary>The node options of the node's current process.</summary>
    public NodeOptions? Options { get; set; }

    /// <summary>Funding spends the channel manager handed to the on-chain watcher (a close, never a splice).</summary>
    public ConcurrentQueue<OutpointSpentEventArgs> FundingSpends { get; } = new();

    /// <summary>The broadcast rows the node's unit of work saved.</summary>
    public List<BroadcastTransactionModel> Broadcasts { get; } = [];

    /// <summary>The accounting events the node's unit of work saved (NL-602).</summary>
    public List<AccountingEventModel> AccountingEvents { get; } = [];

    /// <summary>The watched transactions the node's unit of work saved.</summary>
    public List<WatchedTransactionModel> Watches { get; } = [];

    /// <summary>The watched outpoints the node's unit of work saved.</summary>
    public List<WatchedOutpointModel> WatchedOutpoints { get; } = [];

    /// <summary>
    /// Called at the start of every save with the broadcasts it would store; true makes that save throw with nothing
    /// committed (a crash or a failed database write).
    /// </summary>
    public Func<IReadOnlyList<BroadcastTransactionModel>, bool>? FailSave { get; set; }

    /// <summary>(channel, funding txid, number) marks of SP-I1 the signer received.</summary>
    public ConcurrentBag<(ChannelId, TxId, ulong)> PersistedSpliceCommitments { get; } = [];

    /// <summary>Shared-input signatures the signer made (SP-SIG-01), with whether SP-I1 held.</summary>
    public ConcurrentQueue<TxId> SharedInputSignatures { get; } = new();

    /// <summary>Make the peer's <c>shared_input_signature</c> fail validation (SP-SIG-01).</summary>
    public bool RejectSharedInputSignature { get; set; }

    public SpliceService Service => Node.Services.GetRequiredService<SpliceService>();
    public SpliceDepthWatcher DepthWatcher => Node.Services.GetRequiredService<SpliceDepthWatcher>();
    public QuiescenceService Quiescence => Node.Services.GetRequiredService<QuiescenceService>();
    public IInteractiveTxDriver Driver => Node.Services.GetRequiredService<IInteractiveTxDriver>();

    public FakeSpliceStatePort CreatePort(ILightningSigner signer) =>
        Port = new FakeSpliceStatePort(Name, signer);

    public WalletUtxo Fund(long satoshis)
    {
        var utxo = WalletUtxo.Create(satoshis);
        Contributor.Utxos.Add(utxo);
        return utxo;
    }

    public void Attach(HarnessNode node)
    {
        _node = node;
        node.Services.GetRequiredService<ISpliceStatePort>();
        _ = DepthWatcher;

        // The unit of work of the node gains the interactive-tx rows and the broadcasts; one save commits them all
        var unitOfWork = Mock.Get(node.Services.CreateScope().ServiceProvider.GetRequiredService<IUnitOfWork>());
        var stagedBroadcasts = new List<BroadcastTransactionModel>();
        var stagedWatches = new List<WatchedTransactionModel>();
        var stagedOutpoints = new List<WatchedOutpointModel>();
        var stagedEvents = new List<AccountingEventModel>();
        var accounting = new Mock<IAccountingEventDbRepository>();
        accounting.Setup(a => a.Add(It.IsAny<AccountingEventModel>())).Callback<AccountingEventModel>(stagedEvents.Add);
        unitOfWork.SetupGet(u => u.AccountingEventDbRepository).Returns(accounting.Object);
        var outpoints = new Mock<IWatchedOutpointDbRepository>();
        outpoints.Setup(o => o.Add(It.IsAny<WatchedOutpointModel>())).Callback<WatchedOutpointModel>(stagedOutpoints.Add);
        outpoints.Setup(o => o.GetAsync(It.IsAny<TxId>(), It.IsAny<uint>()))
                 .ReturnsAsync((TxId txId, uint index) =>
                                   WatchedOutpoints.FirstOrDefault(w => w.TransactionId == txId
                                                                     && w.OutputIndex == index));
        node.WatchedTransactions.Setup(w => w.GetByTransactionIdAsync(It.IsAny<TxId>()))
            .ReturnsAsync((TxId txId) => Watches.FirstOrDefault(w => w.TransactionId == txId));
        var broadcasts = new Mock<IBroadcastTransactionDbRepository>();
        broadcasts.Setup(b => b.Add(It.IsAny<BroadcastTransactionModel>()))
                  .Callback<BroadcastTransactionModel>(stagedBroadcasts.Add);
        // NL-520: the splice RBF recency rule reads the latest attempt's creation height from its broadcast row
        broadcasts.Setup(b => b.GetByTransactionIdAsync(It.IsAny<TxId>()))
                  .ReturnsAsync((TxId txId) => Broadcasts.LastOrDefault(b => b.TransactionId == txId));
        node.WatchedTransactions.Setup(w => w.Add(It.IsAny<WatchedTransactionModel>()))
            .Callback<WatchedTransactionModel>(stagedWatches.Add);
        unitOfWork.SetupGet(u => u.InteractiveTxSessionDbRepository).Returns(Sessions);
        unitOfWork.SetupGet(u => u.BroadcastTransactionDbRepository).Returns(broadcasts.Object);
        unitOfWork.SetupGet(u => u.ChannelFundingDbRepository).Returns(FundingRows);
        unitOfWork.SetupGet(u => u.WatchedOutpointDbRepository).Returns(outpoints.Object);
        unitOfWork.SetupGet(u => u.LiquidityPurchaseDbRepository).Returns(Purchases);
        unitOfWork.Setup(u => u.SaveChangesAsync()).Returns(() =>
        {
            try
            {
                if (FailSave?.Invoke(stagedBroadcasts) == true)
                {
                    // The channel state staged by the same save is dropped with it
                    node.Store.Restart();
                    throw new InvalidOperationException("Simulated failed save");
                }

                node.Store.Commit();
            }
            catch
            {
                Sessions.DiscardStaged();
                Port?.DiscardStaged();
                FundingRows.DiscardStaged();
                Purchases.DiscardStaged();
                stagedBroadcasts.Clear();
                stagedWatches.Clear();
                stagedOutpoints.Clear();
                stagedEvents.Clear();
                throw;
            }

            Sessions.Commit();
            Port?.Commit();
            FundingRows.Commit();
            Purchases.Commit();
            Broadcasts.AddRange(stagedBroadcasts);
            Watches.AddRange(stagedWatches);
            WatchedOutpoints.AddRange(stagedOutpoints);
            AccountingEvents.AddRange(stagedEvents);
            stagedBroadcasts.Clear();
            stagedWatches.Clear();
            stagedOutpoints.Clear();
            stagedEvents.Clear();
            return Task.CompletedTask;
        });
    }

    /// <summary>The node's chain monitor reports the splice transaction at its required depth.</summary>
    public void Confirm(TxId spliceTxId, uint height)
    {
        var watch = Watches.Single(w => w.TransactionId == spliceTxId);
        watch.SetHeightAndIndex(height, 1);
        Node.ChainMonitor.Raise(m => m.OnTransactionConfirmed += null, new TransactionConfirmedEventArgs(watch, height));
    }
}

/// <summary>Where a splice-out goes in the harness: one P2WPKH script per node.</summary>
[ExcludeFromCodeCoverage]
internal sealed class FakeSpliceOutDestination : ISpliceOutDestination
{
    public BitcoinScript Script { get; } = new(new Key().PubKey.WitHash.ScriptPubKey.ToBytes());

    public Task<BitcoinScript> ResolveAsync(string? address, CancellationToken cancellationToken = default) =>
        Task.FromResult(Script);
}

/// <summary>
/// Lanes SP1-B/SP1-C's half of a splice, in memory: the fundings of the harness channel (staged, then saved with the
/// node's unit of work, then applied), and splice commitment signatures that are a deterministic function of the funding
/// txid, the commitment number and the signing node (so the peer can check them, and a wrong number or funding fails).
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class FakeSpliceStatePort(string nodeName, ILightningSigner signer) : ISpliceStatePort
{
    private (FundingSet Next, IReadOnlyList<ChannelFunding> Retired)? _staged;
    private FundingSet? _memory;

    /// <summary>The fundings as last saved.</summary>
    public FundingSet? Saved { get; private set; }

    /// <summary>The fundings retired by a saved lock.</summary>
    public List<ChannelFunding> Retired { get; } = [];

    /// <summary>(funding txid, remote commitment number) of every splice commitment we signed (SP-CS-01).</summary>
    public List<(TxId FundingTxId, ulong Number)> Signed { get; } = [];

    /// <summary>(funding txid, local commitment number) of every peer splice commitment we verified (SP-CS-02).</summary>
    public List<(TxId FundingTxId, ulong Number)> Verified { get; } = [];

    /// <summary>Sign the next splice commitment with a wrong key (the peer's check fails).</summary>
    public bool CorruptNextSignature { get; set; }

    public FundingSet GetFundings(ChannelModel channel) =>
        _memory ?? FundingSet.Single(ChannelFunding.FromFundingOutput(channel.FundingOutput!)!);

    public Task<CommitmentSignedMessage> SignSpliceCommitmentAsync(ChannelModel channel, ChannelFunding funding,
                                                                   IUnitOfWork unitOfWork,
                                                                   CancellationToken cancellationToken)
    {
        var number = channel.Commitments!.RemoteCommit.Number;
        Signed.Add((funding.FundingTxId, number));
        var signer = CorruptNextSignature ? "Mallory" : nodeName;
        CorruptNextSignature = false;
        return Task.FromResult(new CommitmentSignedMessage(
                                   new CommitmentSignedPayload(channel.ChannelId, [],
                                                               Signature(funding.FundingTxId, number, signer)),
                                   new FundingTxIdTlv(funding.FundingTxId)));
    }

    public Task ReceiveSpliceCommitmentAsync(ChannelModel channel, ChannelFunding funding,
                                             CommitmentSignedMessage message, IUnitOfWork unitOfWork,
                                             CancellationToken cancellationToken)
    {
        var number = channel.Commitments!.LocalCommit.Number;
        var peer = nodeName == "Alice" ? "Bob" : "Alice";
        if (message.Payload.Signature != Signature(funding.FundingTxId, number, peer))
            throw new SpliceCommitmentException($"the signature is not {peer}'s for {funding.FundingTxId} at {number}");

        Verified.Add((funding.FundingTxId, number));
        return Task.CompletedTask;
    }

    /// <summary>
    /// As lane SP1-B's engine: the peer's verified splice commitment makes the funding pending at once
    /// (<c>ReceiveSpliceCommitment</c>), before any <c>tx_signatures</c>.
    /// </summary>
    public void OnSpliceCommitmentSaved(ChannelModel channel, ChannelFunding funding)
    {
        _memory = AddPending(GetFundings(channel), funding);
        signer.MarkSpliceCommitmentPersisted(channel.ChannelId, funding.FundingTxId,
                                             channel.Commitments!.LocalCommit.Number);
    }

    public FundingSet AddPending(FundingSet fundings, ChannelFunding funding)
    {
        var pending = funding with { Status = ChannelFundingStatus.Pending };
        return fundings.Pending.Any(f => f.FundingTxId == funding.FundingTxId)
                   ? new FundingSet(fundings.Current,
                                    fundings.Pending.Select(f => f.FundingTxId == funding.FundingTxId ? pending : f)
                                            .ToList())
                   : new FundingSet(fundings.Current, [.. fundings.Pending, pending]);
    }

    public (FundingSet Next, IReadOnlyList<ChannelFunding> Retired) Lock(FundingSet fundings, TxId fundingTxId)
    {
        var locked = fundings.Pending.Single(f => f.FundingTxId == fundingTxId);
        var current = locked with
        {
            Status = ChannelFundingStatus.Current,
            LocalBalanceDeltaMsat = 0,
            RemoteBalanceDeltaMsat = 0
        };
        List<ChannelFunding> retired = [fundings.Current with { Status = ChannelFundingStatus.Replaced }];
        retired.AddRange(fundings.Pending.Where(f => f.FundingTxId != fundingTxId)
                                 .Select(f => f with { Status = ChannelFundingStatus.Discarded }));
        return (FundingSet.Single(current), retired);
    }

    public (FundingSet Next, IReadOnlyList<ChannelFunding> Retired) Discard(FundingSet fundings, TxId fundingTxId) =>
        fundings.Pending.FirstOrDefault(f => f.FundingTxId == fundingTxId) is { } discarded
            ? (new FundingSet(fundings.Current, fundings.Pending.Where(f => f.FundingTxId != fundingTxId).ToList()),
               [discarded with { Status = ChannelFundingStatus.Discarded }])
            : (fundings, []);

    public Task StageFundingsAsync(ChannelModel channel, FundingSet next, IReadOnlyList<ChannelFunding> retired,
                                   IUnitOfWork unitOfWork, CancellationToken cancellationToken)
    {
        _staged = (next, retired);
        return Task.CompletedTask;
    }

    public void ApplyFundings(ChannelModel channel, FundingSet next, IReadOnlyList<ChannelFunding> retired) =>
        _memory = next;

    public void Commit()
    {
        if (_staged is not { } staged)
            return;

        Saved = staged.Next;
        Retired.AddRange(staged.Retired);
        _staged = null;
    }

    public void DiscardStaged() => _staged = null;

    public static CompactSignature Signature(TxId fundingTxId, ulong number, string signer)
    {
        var digest = SHA256.HashData([.. (byte[])fundingTxId, .. BitConverter.GetBytes(number),
                                      .. System.Text.Encoding.ASCII.GetBytes(signer)]);
        return new CompactSignature([.. digest, .. SHA256.HashData(digest)]);
    }
}

/// <summary>
/// Lane SP1-C's splice members of <see cref="ILightningSigner"/> over the harness's real signer: funding key rotation
/// (D5, a key per index), SP-I1 (<c>SignSpliceSharedInput</c> refuses until the peer's splice commitment is marked
/// persisted) and deterministic <c>shared_input_signature</c>s the peer checks (SP-SIG-01).
/// </summary>
[ExcludeFromCodeCoverage]
public class SpliceSigningProxy : DispatchProxy
{
    private ILightningSigner _inner = null!;
    private SpliceNode _node = null!;

    internal static ILightningSigner Create(ILightningSigner inner, SpliceNode node)
    {
        var proxy = Create<ILightningSigner, SpliceSigningProxy>();
        var self = (SpliceSigningProxy)(object)proxy;
        self._inner = inner;
        self._node = node;
        return proxy;
    }

    internal static CompactSignature SharedInputSignature(TxId spliceTxId, string signer) =>
        FakeSpliceStatePort.Signature(spliceTxId, ulong.MaxValue, signer);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        switch (targetMethod.Name)
        {
            case nameof(ILightningSigner.GetFundingPubKey):
                {
                    var index = (uint)args![1]!;
                    var seed = SHA256.HashData([.. System.Text.Encoding.ASCII.GetBytes(_node.Name),
                                            .. (byte[])(ChannelId)args[0]!, .. BitConverter.GetBytes(index)]);
                    return (CompactPubKey)new Key(seed).PubKey.ToBytes();
                }
            case nameof(ILightningSigner.RegisterFunding):
                return null;
            case nameof(ILightningSigner.MarkSpliceCommitmentPersisted):
                _node.PersistedSpliceCommitments.Add(((ChannelId)args![0]!, (TxId)args[1]!, (ulong)args[2]!));
                return null;
            case nameof(ILightningSigner.SignSpliceSharedInput):
                {
                    var newFundingTxId = (TxId)args![1]!;
                    if (_node.PersistedSpliceCommitments.All(m => m.Item2 != newFundingTxId))
                        throw new SignerException("[SP-I1] no persisted commitment on the new funding",
                                                  (ChannelId)args[0]!);
                    _node.SharedInputSignatures.Enqueue(newFundingTxId);
                    return SharedInputSignature(((SignedTransaction)args[2]!).TxId, _node.Name);
                }
            case nameof(ILightningSigner.ValidateSpliceSharedInputSignature):
                {
                    var peer = _node.Name == "Alice" ? "Bob" : "Alice";
                    var expected = SharedInputSignature(((SignedTransaction)args![1]!).TxId, peer);
                    if (_node.RejectSharedInputSignature || (CompactSignature)args[3]! != expected)
                        throw new SignerException("invalid shared_input_signature", (ChannelId)args[0]!);
                    return null;
                }
        }

        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }
}

/// <summary>
/// Lane SP1-C's <c>ChannelFundings</c> rows and per-funding commitment slots in memory: every write is staged and
/// becomes visible to <see cref="Committed"/> only when the node's unit of work saves (reads see staged writes, as the
/// change tracker does).
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class InMemoryChannelFundingRepository : IChannelFundingDbRepository
{
    private Dictionary<TxId, ChannelFunding> _staged = [];
    private Dictionary<TxId, LocalCommit> _stagedLocal = [];
    private Dictionary<TxId, (RemoteCommit, CommitmentSignatures?)> _stagedRemote = [];

    /// <summary>The saved rows by funding txid.</summary>
    public Dictionary<TxId, ChannelFunding> Committed { get; private set; } = [];

    /// <summary>Our saved local commitments on pending fundings, by funding txid (SP-I2).</summary>
    public Dictionary<TxId, LocalCommit> CommittedLocal { get; private set; } = [];

    /// <summary>The saved peer commitments on pending fundings with our signatures, by funding txid.</summary>
    public Dictionary<TxId, (RemoteCommit Commit, CommitmentSignatures? Sent)> CommittedRemote { get; private set; } =
        [];

    /// <summary>The funding the last saved lock made current, if any.</summary>
    public ChannelFunding? LockedCurrent { get; private set; }

    private ChannelFunding? _stagedLock;

    public void Commit()
    {
        Committed = new Dictionary<TxId, ChannelFunding>(_staged);
        CommittedLocal = new Dictionary<TxId, LocalCommit>(_stagedLocal);
        CommittedRemote = new Dictionary<TxId, (RemoteCommit, CommitmentSignatures?)>(_stagedRemote);
        LockedCurrent = _stagedLock ?? LockedCurrent;
        _stagedLock = null;
    }

    public void DiscardStaged()
    {
        _staged = new Dictionary<TxId, ChannelFunding>(Committed);
        _stagedLocal = new Dictionary<TxId, LocalCommit>(CommittedLocal);
        _stagedRemote = new Dictionary<TxId, (RemoteCommit, CommitmentSignatures?)>(CommittedRemote);
        _stagedLock = null;
    }

    public Task<IReadOnlyList<ChannelFunding>> GetByChannelIdAsync(ChannelId channelId) =>
        Task.FromResult<IReadOnlyList<ChannelFunding>>(_staged.Values.ToList());

    public Task<FundingSet?> GetFundingSetAsync(ChannelId channelId) =>
        throw new NotSupportedException("The splice harness reads the fundings from the engine");

    public Task UpsertAsync(ChannelId channelId, ChannelFunding funding)
    {
        _staged[funding.FundingTxId] = funding;
        return Task.CompletedTask;
    }

    public Task ApplyLockAsync(ChannelId channelId, ChannelFunding newCurrent, IReadOnlyList<ChannelFunding> retired)
    {
        if (newCurrent.Status != ChannelFundingStatus.Current || !_staged.ContainsKey(newCurrent.FundingTxId))
            throw new InvalidOperationException($"Funding {newCurrent.FundingTxId} cannot be locked");
        if (retired.Any(f => f.Status is not (ChannelFundingStatus.Replaced or ChannelFundingStatus.Discarded)))
            throw new ArgumentException("Retired fundings must be Replaced or Discarded", nameof(retired));

        foreach (var funding in retired)
        {
            _staged[funding.FundingTxId] = funding;
            _stagedLocal.Remove(funding.FundingTxId);
            _stagedRemote.Remove(funding.FundingTxId);
        }

        _staged[newCurrent.FundingTxId] = newCurrent;
        _stagedLocal.Remove(newCurrent.FundingTxId);
        _stagedRemote.Remove(newCurrent.FundingTxId);
        _stagedLock = newCurrent;
        return Task.CompletedTask;
    }

    public Task StageLocalCommitmentAsync(ChannelId channelId, TxId fundingTxId, LocalCommit commit)
    {
        EnsurePending(fundingTxId);
        if (commit.RemoteSignatures is null)
            throw new InvalidOperationException("The local commitment carries no remote signatures");
        _stagedLocal[fundingTxId] = commit;
        return Task.CompletedTask;
    }

    public Task StageRemoteCommitmentAsync(ChannelId channelId, TxId fundingTxId, RemoteCommit commit,
                                           CommitmentSignatures? sentSignatures)
    {
        EnsurePending(fundingTxId);
        _stagedRemote[fundingTxId] = (commit, sentSignatures);
        return Task.CompletedTask;
    }

    public Task StageRemoteNextCommitmentAsync(ChannelId channelId, TxId fundingTxId, RemoteNextCommit? next) =>
        Task.CompletedTask;

    public Task<RemoteNextCommit?> GetRemoteNextCommitmentAsync(ChannelId channelId, TxId fundingTxId) =>
        Task.FromResult<RemoteNextCommit?>(null);

    public Task<LocalCommit?> GetLocalCommitmentAsync(ChannelId channelId, TxId fundingTxId) =>
        Task.FromResult(_stagedLocal.GetValueOrDefault(fundingTxId));

    public Task<(RemoteCommit Commit, CommitmentSignatures? SentSignatures)?> GetRemoteCommitmentAsync(
        ChannelId channelId, TxId fundingTxId) =>
        Task.FromResult<(RemoteCommit, CommitmentSignatures?)?>(
            _stagedRemote.TryGetValue(fundingTxId, out var remote) ? remote : null);

    public Task<bool> StageRevokedCommitmentAsync(ChannelId channelId, TxId fundingTxId, RemoteCommit revoked) =>
        Task.FromResult(false);

    public Task SetDualFundedAsync(ChannelId channelId, LightningMoney localContribution,
                                   LightningMoney remoteContribution) =>
        throw new NotSupportedException();

    public Task<(LightningMoney Local, LightningMoney Remote)?> GetDualFundedContributionsAsync(ChannelId channelId) =>
        Task.FromResult<(LightningMoney, LightningMoney)?>(null);

    private void EnsurePending(TxId fundingTxId)
    {
        if (!_staged.TryGetValue(fundingTxId, out var funding) || funding.Status != ChannelFundingStatus.Pending)
            throw new InvalidOperationException($"Funding {fundingTxId} is not a pending funding of the channel");
    }
}