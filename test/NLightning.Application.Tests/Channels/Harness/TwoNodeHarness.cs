using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Tests.Channels.Harness;

using Application.Channels.Handlers;
using Application.Channels.Handlers.Interfaces;
using Application.Channels.Interfaces;
using Application.Channels.Managers;
using Application.Channels.Reestablish;
using Application.Channels.Services;
using Application.Channels.Switch;
using Application.Protocol.Factories;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Commitments.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.ValueObjects;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Serialization;
using NLightning.Tests.Utils.Mocks;

/// <summary>
/// Two in-process nodes (BOLT2 plan N6-T4): each a real <see cref="ChannelManager"/> with the production
/// normal-operation handlers, engine ports, <c>LocalLightningSigner</c>, commitment/HTLC builders and shachain,
/// joined by in-memory outboxes (each node's <see cref="ChannelManager.OnResponseMessageReady"/> feeds a FIFO the peer
/// drains). Persistence is an in-memory store that commits one transition per save. Every commitment a node signs and
/// every commitment a node verifies is recorded with its txid (invariant I7).
/// </summary>
/// <remarks>
/// Each node's send side is the production <see cref="ChannelOperationsService"/> and <see cref="CommitScheduler"/>
/// (N6-T2, no debounce), publishing through its <see cref="ChannelManager"/>. The HTLC switch records every event and,
/// with <c>localOnlySwitch</c>, hands it to the production <see cref="LocalOnlyHtlcSwitch"/> (with a fake Sphinx peel
/// and a fake error onion, since this project has no serializer for failure messages).
/// </remarks>
[ExcludeFromCodeCoverage]
internal sealed class TwoNodeHarness : IDisposable
{
    public const ulong FundingSatoshis = 2_000_000;
    public const ulong PushSatoshis = 800_000;
    public const uint InitialFeeratePerKw = 2_500;
    public const uint BlockHeight = 500;

    public static readonly ChannelId ChannelId = new(Enumerable.Repeat((byte)0x6B, 32).ToArray());
    public static readonly byte[] Onion = new byte[1366];

    private readonly ChannelParty _aliceParty;
    private readonly ChannelParty _bobParty;
    private readonly TxId _fundingTxId = new(Enumerable.Repeat((byte)0x77, 32).ToArray());
    private readonly CommitmentNumber _obscuring;
    private readonly bool _hasAnchors;
    private readonly bool _localOnlySwitch;
    private readonly Action<HarnessNode, IServiceCollection>? _configureServices;

    public HarnessNode Alice { get; private set; }
    public HarnessNode Bob { get; private set; }

    /// <summary>How many messages were delivered so far (both directions).</summary>
    public int Delivered { get; private set; }

    /// <summary>Stop delivering (without failing) once <see cref="Delivered"/> reaches this; null for no limit.</summary>
    public int? DeliveryBudget { get; set; }

    /// <summary>How many times a node was restarted after a simulated crash.</summary>
    public int Restarts { get; private set; }

    /// <param name="hasAnchors">Anchor outputs.</param>
    /// <param name="localOnlySwitch">Hand events to the production <see cref="LocalOnlyHtlcSwitch"/>.</param>
    /// <param name="aliceState">Alice's channel state: Open (usable on this connection), or a state before Open
    /// (ReadyForUs, ReadyForThem, V1FundingSigned) with no commitment state yet, waiting for channel_ready.</param>
    /// <param name="bobState">Bob's channel state, as <paramref name="aliceState"/>.</param>
    /// <param name="configureServices">Adds or replaces services of each node (last registration wins), e.g. the
    /// close services (N10).</param>
    public TwoNodeHarness(bool hasAnchors = false, bool localOnlySwitch = false,
                          ChannelState aliceState = ChannelState.Open, ChannelState bobState = ChannelState.Open,
                          Action<HarnessNode, IServiceCollection>? configureServices = null)
    {
        _hasAnchors = hasAnchors;
        _localOnlySwitch = localOnlySwitch;
        _configureServices = configureServices;
        Alice = new HarnessNode("Alice", 0xA1, localOnlySwitch, configureServices: configureServices);
        Bob = new HarnessNode("Bob", 0xB0, localOnlySwitch, configureServices: configureServices);
        Alice.Peer = Bob;
        Bob.Peer = Alice;

        // Per-side values differ on purpose (dust limit, to_self_delay) so a direction mix-up changes the txid
        _aliceParty = new ChannelParty(LightningMoney.Satoshis(546), LightningMoney.Satoshis(20_000),
                                       LightningMoney.MilliSatoshis(1_000), 30,
                                       LightningMoney.Satoshis(FundingSatoshis), 144);
        _bobParty = new ChannelParty(LightningMoney.Satoshis(600), LightningMoney.Satoshis(20_000),
                                     LightningMoney.MilliSatoshis(1_000), 30,
                                     LightningMoney.Satoshis(FundingSatoshis), 100);
        _obscuring = new CommitmentNumber(Alice.Basepoints.PaymentBasepoint, Bob.Basepoints.PaymentBasepoint,
                                          new Sha256());

        OpenOrRegister(Alice, aliceState);
        OpenOrRegister(Bob, bobState);
    }

    private void OpenOrRegister(HarnessNode node, ChannelState state)
    {
        if (state == ChannelState.Open)
            node.Open(CreateChannelFor(node));
        else
            node.RegisterPending(CreateChannelFor(node, state));
    }

    /// <summary>
    /// Delivers queued messages, one per side in turn (so messages cross like on a real link), until both outboxes are
    /// empty and neither commit scheduler has a signature waiting. A node whose store crashed is restarted and the link
    /// reconnected (<see cref="RecoverAsync"/>). Stops early, without failing, once <see cref="DeliveryBudget"/> is
    /// reached.
    /// </summary>
    public async Task PumpAsync()
    {
        for (var steps = 0; steps < 10_000; steps++)
        {
            await RecoverAsync();
            await Alice.Scheduler.WhenIdleAsync();
            await Bob.Scheduler.WhenIdleAsync();
            await RecoverAsync();
            if (DeliveryBudget is { } budget && Delivered >= budget)
                return;

            var aliceSent = await DeliverAsync(Alice);
            await RecoverAsync();
            if (DeliveryBudget is { } budgetAfterAlice && Delivered >= budgetAfterAlice)
                return;

            var bobSent = await DeliverAsync(Bob);
            if (aliceSent || bobSent)
                continue;

            await Alice.Scheduler.WhenIdleAsync();
            await Bob.Scheduler.WhenIdleAsync();
            await RecoverAsync();
            if (Alice.OutboxIsEmpty && Bob.OutboxIsEmpty)
                return;
        }

        throw new InvalidOperationException("The message exchange did not converge");
    }

    /// <summary>
    /// The link drops: messages in flight are lost both ways, the peer managers tell the channel managers (the
    /// connection changed, then the last message of the old connection was handled), and nothing the nodes raise
    /// reaches the peer until <see cref="ReconnectAsync"/>.
    /// </summary>
    public async Task DisconnectAsync()
    {
        foreach (var node in new[] { Alice, Bob })
        {
            node.PeerAlive = false;
            node.DropOutbox();
            node.ChannelManager.OnPeerConnectionChanged(node.Peer.NodeId);
        }

        await Alice.Scheduler.WhenIdleAsync();
        await Bob.Scheduler.WhenIdleAsync();
        await Alice.ChannelManager.OnPeerDisconnectedAsync(Bob.NodeId);
        await Bob.ChannelManager.OnPeerDisconnectedAsync(Alice.NodeId);
        Alice.DropOutbox();
        Bob.DropOutbox();
    }

    /// <summary>A new connection: each side sends its channel_reestablish first (BOLT 2), as the peer manager does.
    /// </summary>
    public async Task ReconnectAsync()
    {
        Alice.PeerAlive = true;
        Bob.PeerAlive = true;
        Assert.Empty(await Alice.ChannelManager.OnPeerConnectedAsync(Bob.NodeId));
        Assert.Empty(await Bob.ChannelManager.OnPeerConnectedAsync(Alice.NodeId));
    }

    /// <summary>
    /// Restarts every node whose store crashed (a new process on the same seed and store: only what was saved
    /// survives), and reconnects the link. Nothing happens when no store crashed.
    /// </summary>
    public async Task RecoverAsync()
    {
        if (!Alice.Store.Crashed && !Bob.Store.Crashed)
            return;

        await DisconnectAsync();
        if (Alice.Store.Crashed)
            Alice = await RestartAsync(Alice);
        if (Bob.Store.Crashed)
            Bob = await RestartAsync(Bob);

        Alice.Peer = Bob;
        Bob.Peer = Alice;
        await ReconnectAsync();
    }

    /// <summary>
    /// Restarts <paramref name="node"/> from an old copy of its store (an operator restoring a backup), link down.
    /// </summary>
    /// <returns>The restarted node (also set as <see cref="Alice"/>/<see cref="Bob"/>).</returns>
    public async Task<HarnessNode> RestartFromBackupAsync(HarnessNode node, InMemoryChannelStateStore.Backup backup)
    {
        await DisconnectAsync();
        node.Store.RestoreBackup(backup);
        var restarted = await RestartAsync(node);
        if (node == Alice)
            Alice = restarted;
        else
            Bob = restarted;

        Alice.Peer = Bob;
        Bob.Peer = Alice;
        return restarted;
    }

    private async Task<HarnessNode> RestartAsync(HarnessNode crashed)
    {
        Restarts++;
        await crashed.Scheduler.WhenIdleAsync();
        crashed.Dispose();
        crashed.Store.Restart();

        var restarted = new HarnessNode(crashed.Name, (byte)crashed.KeyIndex, _localOnlySwitch, crashed,
                                        _configureServices)
        {
            Peer = crashed.Peer,
            PeerAlive = false
        };
        await restarted.RestoreAsync(CreateChannelFor(restarted));
        return restarted;
    }

    private async Task<bool> DeliverAsync(HarnessNode node)
    {
        try
        {
            var delivered = await node.DeliverNextAsync();
            if (delivered)
                Delivered++;
            return delivered;
        }
        catch (SimulatedCrashException)
        {
            // The receiver crashed while handling it: RecoverAsync restarts it
            Delivered++;
            return true;
        }
    }

    private ChannelModel CreateChannelFor(HarnessNode node, ChannelState state = ChannelState.Open)
    {
        var isAlice = node.Name == "Alice";
        return CreateChannel(node, node.Peer, isAlice ? _aliceParty : _bobParty, isAlice ? _bobParty : _aliceParty,
                             isAlice, _fundingTxId, _obscuring, _hasAnchors, state);
    }

    /// <summary>The <c>reason</c> the fake error onion returns: a marker, the failure code and its data.</summary>
    public static byte[] FakeErrorPacket(FailureMessage message) =>
        [0xEE, (byte)((ushort)message.Code >> 8), (byte)message.Code, .. message.Data.ToArray()];

    public static Secret Preimage(int tag)
    {
        var bytes = new byte[32];
        BitConverter.GetBytes(tag + 1).CopyTo(bytes, 0);
        return new Secret(bytes);
    }

    public static Hash Hash(Secret preimage)
    {
        using var sha256 = new Sha256();
        var hash = new byte[32];
        sha256.AppendData(preimage);
        sha256.GetHashAndReset(hash);
        return new Hash(hash);
    }

    public void Dispose()
    {
        Alice.Dispose();
        Bob.Dispose();
    }

    private static ChannelModel CreateChannel(HarnessNode self, HarnessNode peer, ChannelParty local,
                                              ChannelParty remote, bool isInitiator, TxId fundingTxId,
                                              CommitmentNumber obscuring, bool hasAnchors, ChannelState state)
    {
        var channelParams = new ChannelParams(local, remote, LightningMoney.Satoshis(InitialFeeratePerKw), 3,
                                              hasAnchors, FeatureSupport.No);
        var fundingOutput = new FundingOutputInfo(LightningMoney.Satoshis(FundingSatoshis),
                                                  self.Basepoints.FundingPubKey, peer.Basepoints.FundingPubKey,
                                                  fundingTxId, 0);
        var localKeySet = new ChannelKeySetModel(self.KeyIndex, self.Basepoints.FundingPubKey,
                                                 self.Basepoints.RevocationBasepoint, self.Basepoints.PaymentBasepoint,
                                                 self.Basepoints.DelayedPaymentBasepoint,
                                                 self.Basepoints.HtlcBasepoint, self.Point(0));
        var remoteKeySet = new ChannelKeySetModel(0, peer.Basepoints.FundingPubKey, peer.Basepoints.RevocationBasepoint,
                                                  peer.Basepoints.PaymentBasepoint,
                                                  peer.Basepoints.DelayedPaymentBasepoint,
                                                  peer.Basepoints.HtlcBasepoint, peer.Point(0));
        var localSat = isInitiator ? FundingSatoshis - PushSatoshis : PushSatoshis;
        return new ChannelModel(channelParams, ChannelId, obscuring, fundingOutput, isInitiator, null, null,
                                LightningMoney.Satoshis(localSat), localKeySet, 0, 0,
                                LightningMoney.Satoshis(FundingSatoshis - localSat), remoteKeySet, 0,
                                peer.NodeId, 0, state, ChannelVersion.V1);
    }
}

/// <summary>One side of <see cref="TwoNodeHarness"/>.</summary>
[ExcludeFromCodeCoverage]
internal sealed class HarnessNode : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly InMemoryChannelRepository _channels = new();
    private readonly ConcurrentQueue<IChannelMessage> _outbox = new();
    private readonly ChannelLockProvider _lockProvider = new();

    public string Name { get; }
    public uint KeyIndex { get; }
    public CompactPubKey NodeId { get; }
    public ChannelBasepoints Basepoints { get; }
    public ILightningSigner Signer { get; }
    public ChannelManager ChannelManager { get; }
    public IChannelOperations Operations { get; }
    public ICommitScheduler Scheduler { get; }
    public InMemoryChannelStateStore Store { get; }
    public ReestablishTracker Tracker { get; }

    /// <summary>The watched-transaction rows the node's unit of work stages (a loose mock).</summary>
    public Mock<IWatchedTransactionDbRepository> WatchedTransactions { get; } = new();

    /// <summary>
    /// Whether the peer is connected: the liveness probe answers it (with the channel's link pinned at
    /// <see cref="Open"/>), and while it is false every message this node raises is dropped, as
    /// <c>PeerManager</c> drops a message for a peer that is not connected.
    /// </summary>
    public bool PeerAlive { get; set; } = true;

    /// <summary>Messages raised while <see cref="PeerAlive"/> was false (lost, like in production).</summary>
    public List<IChannelMessage> Dropped { get; } = [];

    public bool OutboxIsEmpty => _outbox.IsEmpty;
    public HarnessNode Peer { get; set; } = null!;

    /// <summary>Remote commitments this node signed: (number, txid). Kept across restarts.</summary>
    public List<(ulong Number, TxId TxId)> Signed { get; } = [];

    /// <summary>Local commitments this node verified: (number, txid). Kept across restarts.</summary>
    public List<(ulong Number, TxId TxId)> Verified { get; } = [];

    /// <summary>Every domain event handed to this node's HTLC switch, in order. Kept across restarts.</summary>
    public List<IChannelDomainEvent> Events { get; } = [];

    /// <summary>Every message this node received, in order (type only). Kept across restarts.</summary>
    public List<IChannelMessage> Received { get; } = [];

    public ChannelModel Channel => _channels.TryGetChannel(TwoNodeHarness.ChannelId, out var channel)
                                       ? channel
                                       : throw new InvalidOperationException("No channel");

    public ChannelCommitments State => Channel.Commitments!;

    /// <summary>The node's services (to resolve what the harness does not expose).</summary>
    public IServiceProvider Services => _provider;

    /// <param name="name">The node's name.</param>
    /// <param name="seedTag">The key seed (and key index).</param>
    /// <param name="localOnlySwitch">Hand events to the production <see cref="LocalOnlyHtlcSwitch"/>.</param>
    /// <param name="previous">The crashed node this one restarts: its store and records are kept.</param>
    /// <param name="configureServices">Adds or replaces services before the provider is built.</param>
    public HarnessNode(string name, byte seedTag, bool localOnlySwitch = false, HarnessNode? previous = null,
                       Action<HarnessNode, IServiceCollection>? configureServices = null)
    {
        Name = name;
        KeyIndex = seedTag;
        Store = previous?.Store ?? new InMemoryChannelStateStore();
        if (previous is not null)
        {
            Signed = previous.Signed;
            Verified = previous.Verified;
            Events = previous.Events;
            Received = previous.Received;
            Dropped = previous.Dropped;
            Lost = previous.Lost;
        }

        var rootKey = new ExtKey(new Key(Enumerable.Repeat(seedTag, 32).ToArray()), new byte[32]);
        var secureKeyManager = new Mock<ISecureKeyManager>();
        secureKeyManager.Setup(x => x.GetChannelKeyAtIndex(It.IsAny<uint>()))
                        .Returns((uint index) => (ExtPrivKey)rootKey.Derive((int)index, true).ToBytes());

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.ChannelStateDbRepository).Returns(Store);
        unitOfWork.SetupGet(u => u.RemoteShachainDbRepository).Returns(Store);
        // The channel row exists (channel_ready's handler checks it before it saves the state change)
        var channelDb = new Mock<IChannelDbRepository>();
        channelDb.Setup(r => r.GetByIdAsync(It.IsAny<ChannelId>()))
                 .ReturnsAsync((ChannelId id) => _channels.TryGetChannel(id, out var channel) ? channel : null);
        unitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(channelDb.Object);
        unitOfWork.SetupGet(u => u.WatchedTransactionDbRepository).Returns(WatchedTransactions.Object);
        unitOfWork.Setup(u => u.SaveChangesAsync()).Returns(() =>
        {
            Store.Commit();
            return Task.CompletedTask;
        });

        var blockchainMonitor = new Mock<IBlockchainMonitor>();
        blockchainMonitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(TwoNodeHarness.BlockHeight);

        // A final-hop peel with a per-node secret; the error onion only marks the failure it was given
        var failureOnion = new Mock<IFailureOnionService>();
        failureOnion.Setup(f => f.CreateErrorPacket(It.IsAny<Secret>(), It.IsAny<FailureMessage>(), It.IsAny<int>()))
                    .Returns((Secret _, FailureMessage message, int _) => TwoNodeHarness.FakeErrorPacket(message));

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(new NodeOptions { EnableHtlcs = true }));
        services.AddSingleton(secureKeyManager.Object);
        services.AddSingleton(new Mock<IUtxoMemoryRepository>().Object);
        services.AddSingleton<IChannelMemoryRepository>(_channels);
        services.AddBitcoinInfrastructure();
        services.AddSingleton<ICommitmentTransactionModelFactory, CommitmentTransactionModelFactory>();
        services.AddCommitmentEngineServices();
        services.AddChannelStateTransitionServices();
        services.AddSingleton<ICommitmentSigner>(sp => new RecordingSigner(
                                                     sp.GetRequiredService<CommitmentSigningService>(), _channels,
                                                     Signed));
        services.AddSingleton<ICommitmentVerifier>(sp => new RecordingVerifier(
                                                       sp.GetRequiredService<CommitmentSigningService>(), _channels,
                                                       Verified));
        services.AddSingleton<IMessageFactory, MessageFactory>();
        services.AddSerializationInfrastructureServices();
        services.AddSingleton(blockchainMonitor.Object);
        services.AddSingleton<ISphinxService>(new FinalHopSphinx(new Secret(Enumerable.Repeat(seedTag, 32).ToArray())));
        services.AddSingleton(failureOnion.Object);
        services.AddSingleton<IChannelLockProvider>(_lockProvider);
        services.AddSingleton<IChannelMessagePublisher>(new LazyPublisher(this));
        services.AddSingleton<ReestablishTracker>();
        services.AddScoped<ReestablishService>();
        services.AddSingleton<IPeerLivenessProbe>(sp => new ReestablishGatedLivenessProbe(
                                                      new FlagProbe(this), sp.GetRequiredService<ReestablishTracker>()));
        services.AddChannelOperationsServices();
        services.Configure<CommitSchedulerOptions>(o => o.Debounce = TimeSpan.Zero);
        services.AddSingleton<LocalOnlyHtlcSwitch>();
        services.AddSingleton<IHtlcSwitch>(sp => new RecordingSwitch(
                                               Events,
                                               localOnlySwitch ? sp.GetRequiredService<LocalOnlyHtlcSwitch>() : null));
        services.AddScoped(_ => unitOfWork.Object);
        services.AddScoped<IChannelMessageHandler<UpdateAddHtlcMessage>, UpdateAddHtlcMessageHandler>();
        services.AddScoped<IChannelMessageHandler<UpdateFulfillHtlcMessage>, UpdateFulfillHtlcMessageHandler>();
        services.AddScoped<IChannelMessageHandler<UpdateFailHtlcMessage>, UpdateFailHtlcMessageHandler>();
        services
           .AddScoped<IChannelMessageHandler<UpdateFailMalformedHtlcMessage>, UpdateFailMalformedHtlcMessageHandler>();
        services.AddScoped<IChannelMessageHandler<CommitmentSignedMessage>, CommitmentSignedMessageHandler>();
        services.AddScoped<IChannelMessageHandler<RevokeAndAckMessage>, RevokeAndAckMessageHandler>();
        services.AddScoped<IChannelMessageHandler<UpdateFeeMessage>, UpdateFeeMessageHandler>();
        services.AddScoped<IChannelMessageHandler<ChannelReestablishMessage>, ChannelReestablishMessageHandler>();
        services.AddScoped<IChannelMessageHandler<ChannelReadyMessage>, ChannelReadyMessageHandler>();
        configureServices?.Invoke(this, services);
        _provider = services.BuildServiceProvider();
        Tracker = _provider.GetRequiredService<ReestablishTracker>();

        Signer = _provider.GetRequiredService<ILightningSigner>();
        Basepoints = Signer.GetChannelBasepoints(KeyIndex);
        NodeId = new Key(Enumerable.Repeat(seedTag, 32).ToArray()).PubKey.ToBytes();
        ChannelManager = new ChannelManager(new Mock<IBlockchainMonitor>().Object, _lockProvider, _channels,
                                            NullLogger<ChannelManager>.Instance, Signer, _provider);
        ChannelManager.OnResponseMessageReady += (_, args) =>
        {
            if (PeerAlive)
                _outbox.Enqueue(args.ResponseMessage);
            else
                Dropped.Add(args.ResponseMessage);
        };
        Operations = _provider.GetRequiredService<IChannelOperations>();
        Scheduler = _provider.GetRequiredService<ICommitScheduler>();
    }

    public CompactPubKey Point(ulong commitmentNumber) => Signer.GetPerCommitmentPoint(KeyIndex, commitmentNumber);

    /// <summary>Registers the opened channel and gives it its first commitment state (as channel_ready would).</summary>
    public void Open(ChannelModel channel)
    {
        Signer.RegisterChannel(channel.ChannelId, channel.GetSigningInfo());
        channel.UpdateCommitments(ChannelStateTransitionService.CreateInitialCommitments(channel, Peer.Point(0),
                                                                                         Peer.Point(1)));
        _channels.AddChannel(channel);
        Store.Seed(channel.Commitments!);
        Tracker.MarkOpened(channel.ChannelId, channel.RemoteNodeId);
        _provider.GetRequiredService<IPeerLivenessProbe>().MarkLinkUp(channel.ChannelId, channel.RemoteNodeId);
    }

    /// <summary>
    /// Registers a channel still waiting for channel_ready (no commitment state, not usable): channel_ready builds
    /// its first snapshot, as in production.
    /// </summary>
    public void RegisterPending(ChannelModel channel)
    {
        Signer.RegisterChannel(channel.ChannelId, channel.GetSigningInfo());
        _channels.AddChannel(channel);
    }

    /// <summary>
    /// Loads the channel as a restarted node does: the saved snapshot, sent diff and last-sent order on a fresh
    /// channel model, registered through <see cref="ChannelManager.RegisterExistingChannelAsync"/> (signer, revert
    /// of the peer's unsigned updates, event replay).
    /// </summary>
    public async Task RestoreAsync(ChannelModel channel)
    {
        channel.UpdateCommitments(Store.Committed!, new ChannelStateExtras
        {
            SentCommitDiff = Store.CommittedSentCommitDiff,
            LastSent = Store.CommittedLastSent
        });
        await ChannelManager.RegisterExistingChannelAsync(channel);
    }

    /// <summary>Messages lost in flight when the link dropped (<see cref="DropOutbox"/>). Kept across restarts.</summary>
    public List<IChannelMessage> Lost { get; } = [];

    /// <summary>Loses every message this node queued and did not deliver yet.</summary>
    public void DropOutbox()
    {
        while (_outbox.TryDequeue(out var message))
            Lost.Add(message);
    }

    /// <summary>Hands the oldest queued message to the peer's channel manager.</summary>
    /// <returns>False when nothing was queued.</returns>
    public async Task<bool> DeliverNextAsync()
    {
        if (!_outbox.TryDequeue(out var message))
            return false;

        Peer.Received.Add(message);
        await Peer.ChannelManager.HandleChannelMessageAsync(message, new FeatureOptions(), NodeId);
        return true;
    }

    public void Dispose() => _provider.Dispose();

    /// <summary>Publishes through the node's channel manager, which is built after the provider.</summary>
    private sealed class LazyPublisher(HarnessNode node) : IChannelMessagePublisher
    {
        public void Publish(CompactPubKey peerPubKey, IReadOnlyList<IChannelMessage> messages) =>
            node.ChannelManager.Publish(peerPubKey, messages);
    }

    /// <summary>"Ping before commit" answered by <see cref="PeerAlive"/> for a channel marked up.</summary>
    private sealed class FlagProbe(HarnessNode node) : IPeerLivenessProbe
    {
        private readonly ConcurrentDictionary<ChannelId, byte> _links = new();

        public Task<bool> IsAliveAsync(ChannelId channelId, CompactPubKey peerPubKey,
                                       CancellationToken cancellationToken = default) =>
            Task.FromResult(node.PeerAlive && _links.ContainsKey(channelId));

        public void MarkLinkUp(ChannelId channelId, CompactPubKey peerPubKey) => _links[channelId] = 0;
    }

    /// <summary>Records every event, then hands it to the production switch when there is one.</summary>
    private sealed class RecordingSwitch(List<IChannelDomainEvent> events, IHtlcSwitch? inner) : IHtlcSwitch
    {
        public async Task HandleAsync(IChannelDomainEvent channelEvent, CancellationToken cancellationToken)
        {
            lock (events)
                events.Add(channelEvent);
            if (inner is not null)
                await inner.HandleAsync(channelEvent, cancellationToken);
        }
    }

    /// <summary>Every onion peels as a final hop with this node's fixed shared secret (Moq can't mock span arguments).
    /// </summary>
    private sealed class FinalHopSphinx(Secret sharedSecret) : ISphinxService
    {
        public OnionPacket Construct(IReadOnlyList<OnionHop> hops, PrivKey sessionKey,
                                     ReadOnlySpan<byte> associatedData, int hopPayloadsLength,
                                     OnionPacketKind packetKind) => throw new NotSupportedException();

        public ConstructedOnion ConstructWithSharedSecrets(IReadOnlyList<OnionHop> hops, PrivKey sessionKey,
                                                           ReadOnlySpan<byte> associatedData, int hopPayloadsLength,
                                                           OnionPacketKind packetKind) =>
            throw new NotSupportedException();

        public IReadOnlyList<Secret> ComputeSharedSecrets(IReadOnlyList<CompactPubKey> nodeIds, PrivKey sessionKey) =>
            throw new NotSupportedException();

        public PeeledOnion PeelAsLocalNode(OnionPacket packet, ReadOnlySpan<byte> associatedData,
                                           CompactPubKey? pathKey, OnionPacketKind packetKind) =>
            new(new byte[] { 2, 0 }, sharedSecret, null);

        public PeeledOnion Peel(OnionPacket packet, ReadOnlySpan<byte> associatedData, PrivKey nodeKey,
                                CompactPubKey? pathKey, OnionPacketKind packetKind) =>
            throw new NotSupportedException();
    }

    /// <summary>The production signer port, recording the txid of every commitment it signs.</summary>
    private sealed class RecordingSigner(CommitmentSigningService service, IChannelMemoryRepository channels,
                                         List<(ulong, TxId)> signed) : ICommitmentSigner
    {
        private readonly EngineCommitmentSignerPort _inner = new(service, channels);

        public CommitmentSignatures SignRemoteCommitment(ChannelId channelId, ulong number, CommitmentSpec spec,
                                                         CompactPubKey remotePerCommitmentPoint)
        {
            channels.TryGetChannel(channelId, out var channel);
            var txId = service.SignRemoteCommitment(channel!, CommitmentTxSpec.FromCommitmentSpec(spec), number,
                                                    remotePerCommitmentPoint).CommitmentTxId;
            signed.Add((number, txId));
            return _inner.SignRemoteCommitment(channelId, number, spec, remotePerCommitmentPoint);
        }
    }

    /// <summary>The production verifier port, recording the txid of every commitment it accepts.</summary>
    private sealed class RecordingVerifier(CommitmentSigningService service, IChannelMemoryRepository channels,
                                           List<(ulong, TxId)> verified) : ICommitmentVerifier
    {
        private readonly EngineCommitmentVerifierPort _inner =
            new(service, channels, NullLogger<EngineCommitmentVerifierPort>.Instance);

        public bool VerifyLocalCommitment(ChannelId channelId, ulong number, CommitmentSpec spec,
                                          CommitmentSignatures signatures)
        {
            if (!_inner.VerifyLocalCommitment(channelId, number, spec, signatures))
                return false;

            channels.TryGetChannel(channelId, out var channel);
            var txId = service.VerifyLocalCommitment(channel!, CommitmentTxSpec.FromCommitmentSpec(spec), number,
                                                     signatures.Signature, signatures.HtlcSignatures).CommitmentTxId;
            verified.Add((number, txId));
            return true;
        }
    }
}

/// <summary>
/// The commitment state and the peer's shachain of one node, staged by <c>ApplyAsync</c>/<c>SaveAsync</c> and committed
/// by the unit of work's save (one transition per save).
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class InMemoryChannelStateStore : IChannelStateDbRepository, IRemoteShachainDbRepository
{
    private ChannelCommitments? _staged;
    private IReadOnlyList<ShachainEntry>? _stagedShachain;
    private ChannelStateExtras? _stagedExtras;
    private readonly List<HtlcRecord> _stagedSettled = [];
    private readonly List<HtlcRecord> _settled = [];
    private int _saveAttempts;

    private readonly Dictionary<HtlcKey, Secret> _onionSecrets = [];
    private readonly Dictionary<(ChannelId, HtlcKey), HtlcOrigin> _origins = [];
    private readonly List<HtlcKey> _stagedPrunes = [];

    public ChannelCommitments? Committed { get; private set; }

    /// <summary>The saved <c>SentCommitDiff</c> (cleared once the peer revoked, as the repository does).</summary>
    public ReadOnlyMemory<byte>? CommittedSentCommitDiff { get; private set; }

    public LastSentCommitmentMessage CommittedLastSent { get; private set; }

    /// <summary>
    /// The 1-based save (counted from the last <see cref="Restart"/>) that throws <see cref="SimulatedCrashException"/>
    /// instead of saving; null never crashes. Every later save throws too, until <see cref="Restart"/>: the process is
    /// dead.
    /// </summary>
    public int? CrashAtSave { get; set; }

    public bool Crashed { get; private set; }

    /// <summary>The archived HTLCs whose pruning was saved.</summary>
    public List<HtlcKey> Pruned { get; } = [];
    public IReadOnlyList<ShachainEntry> CommittedShachain { get; private set; } = [];
    public int Saves { get; private set; }

    public void Seed(ChannelCommitments commitments) => Committed = commitments;

    /// <summary>A copy of what is saved now.</summary>
    public sealed record Backup(ChannelCommitments? Committed, ReadOnlyMemory<byte>? SentCommitDiff,
                                LastSentCommitmentMessage LastSent, IReadOnlyList<ShachainEntry> Shachain);

    public Backup TakeBackup() => new(Committed, CommittedSentCommitDiff, CommittedLastSent, CommittedShachain);

    /// <summary>Replaces everything saved with <paramref name="backup"/> (the settled archive is dropped).</summary>
    public void RestoreBackup(Backup backup)
    {
        Committed = backup.Committed;
        CommittedSentCommitDiff = backup.SentCommitDiff;
        CommittedLastSent = backup.LastSent;
        CommittedShachain = backup.Shachain;
        _settled.Clear();
    }

    /// <summary>A new process on this store: what was staged is gone, saving works again.</summary>
    public void Restart()
    {
        DiscardStaged();
        Crashed = false;
        CrashAtSave = null;
        _saveAttempts = 0;
    }

    public void Commit()
    {
        _saveAttempts++;
        if (Crashed || _saveAttempts == CrashAtSave)
        {
            Crashed = true;
            DiscardStaged();
            throw new SimulatedCrashException(_saveAttempts);
        }

        if (_staged is not null)
        {
            Committed = _staged;
            if (_stagedExtras?.SentCommitDiff is { } diff)
                CommittedSentCommitDiff = diff.ToArray();
            if (_staged.RemoteNextCommit is null)
                CommittedSentCommitDiff = null;
            if (_stagedExtras?.LastSent is { } lastSent)
                CommittedLastSent = lastSent;
        }

        _settled.AddRange(_stagedSettled);
        _settled.RemoveAll(h => _stagedPrunes.Contains(h.Key));
        if (_stagedShachain is not null)
            CommittedShachain = _stagedShachain;
        Pruned.AddRange(_stagedPrunes);
        DiscardStaged();
        Saves++;
    }

    private void DiscardStaged()
    {
        _staged = null;
        _stagedShachain = null;
        _stagedExtras = null;
        _stagedSettled.Clear();
        _stagedPrunes.Clear();
    }

    public Task InitializeAsync(ChannelCommitments snapshot, ChannelStateExtras? extras = null)
    {
        _staged = snapshot;
        return Task.CompletedTask;
    }

    public Task ApplyAsync(ChannelCommitments next, ChannelTransition transition, ChannelStateExtras? extras = null)
    {
        _staged = next;
        _stagedExtras = extras;
        _stagedSettled.AddRange(transition.SettledHtlcs);
        if (extras?.RemoteShachain is { } shachain)
            _stagedShachain = shachain;
        return Task.CompletedTask;
    }

    public Task<PersistedChannelState?> LoadAsync(ChannelId channelId, CommitmentParams @params) =>
        Task.FromResult<PersistedChannelState?>(
            Committed is null
                ? null
                : new PersistedChannelState(Committed, _settled.ToList(), CommittedSentCommitDiff, CommittedLastSent,
                                            CommittedShachain));

    public Task SetOnionSharedSecretAsync(ChannelId channelId, HtlcKey htlc, Secret sharedSecret)
    {
        _onionSecrets[htlc] = sharedSecret;
        return Task.CompletedTask;
    }

    public Task<Secret?> GetOnionSharedSecretAsync(ChannelId channelId, HtlcKey htlc) =>
        Task.FromResult(_onionSecrets.TryGetValue(htlc, out var secret) ? secret : (Secret?)null);

    public Task SetHtlcOriginAsync(ChannelId channelId, HtlcKey htlc, HtlcOrigin origin)
    {
        _origins[(channelId, htlc)] = origin;
        return Task.CompletedTask;
    }

    public Task<HtlcOrigin?> GetHtlcOriginAsync(ChannelId channelId, HtlcKey htlc) =>
        Task.FromResult(_origins.TryGetValue((channelId, htlc), out var origin) ? origin : (HtlcOrigin?)null);

    public Task<IReadOnlyList<(ChannelId ChannelId, HtlcKey Htlc)>> FindHtlcsByOriginAsync(HtlcOrigin origin) =>
        Task.FromResult<IReadOnlyList<(ChannelId ChannelId, HtlcKey Htlc)>>(
            _origins.Where(pair => pair.Value.Equals(origin)).Select(pair => pair.Key).ToList());

    public Task PruneSettledHtlcsAsync(ChannelId channelId, IEnumerable<HtlcKey> htlcs)
    {
        _stagedPrunes.AddRange(htlcs);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ShachainEntry>> GetByChannelIdAsync(ChannelId channelId) =>
        Task.FromResult(CommittedShachain);

    public Task SaveAsync(ChannelId channelId, IReadOnlyList<ShachainEntry> entries)
    {
        _stagedShachain = entries;
        return Task.CompletedTask;
    }
}

/// <summary>A minimal <see cref="IChannelMemoryRepository"/> for one node (real channels only).</summary>
[ExcludeFromCodeCoverage]
internal sealed class InMemoryChannelRepository : IChannelMemoryRepository
{
    private readonly Dictionary<ChannelId, ChannelModel> _channels = [];

    public event EventHandler<ChannelUpgradedEventArgs>? OnChannelUpgraded;
    public event EventHandler<ChannelUpdatedEventArgs>? OnChannelUpdated;

    public bool TryGetChannel(ChannelId channelId, [MaybeNullWhen(false)] out ChannelModel channel) =>
        _channels.TryGetValue(channelId, out channel);

    public List<ChannelModel> FindChannels(Func<ChannelModel, bool> predicate) =>
        _channels.Values.Where(predicate).ToList();

    public bool TryGetChannelState(ChannelId channelId, out ChannelState channelState)
    {
        channelState = _channels.TryGetValue(channelId, out var channel) ? channel.State : ChannelState.None;
        return channel is not null;
    }

    public void AddChannel(ChannelModel channel) => _channels[channel.ChannelId] = channel;

    public void UpdateChannel(ChannelModel channel)
    {
        _channels[channel.ChannelId] = channel;
        OnChannelUpdated?.Invoke(this, null!);
    }

    public bool TryRemoveChannel(ChannelId channelId) => _channels.Remove(channelId);

    public bool TryGetTemporaryChannel(CompactPubKey compactPubKey, ChannelId channelId,
                                       [MaybeNullWhen(false)] out ChannelModel channel)
    {
        channel = null;
        return false;
    }

    public bool TryGetTemporaryChannelState(CompactPubKey compactPubKey, ChannelId channelId,
                                            out ChannelState channelState)
    {
        channelState = ChannelState.None;
        return false;
    }

    public void AddTemporaryChannel(CompactPubKey compactPubKey, ChannelModel channel) =>
        throw new NotSupportedException();

    public void UpdateTemporaryChannel(CompactPubKey compactPubKey, ChannelModel channel) =>
        throw new NotSupportedException();

    public bool TryRemoveTemporaryChannel(CompactPubKey compactPubKey, ChannelId channelId) => false;

    public void UpgradeChannel(ChannelId oldChannelId, ChannelModel tempChannel)
    {
        OnChannelUpgraded?.Invoke(this, null!);
        throw new NotSupportedException();
    }
}