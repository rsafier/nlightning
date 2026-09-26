using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NBitcoin;

namespace NLightning.Application.Tests.Channels.Harness;

using Application.Channels.Handlers;
using Application.Channels.Handlers.Interfaces;
using Application.Channels.Interfaces;
using Application.Channels.Managers;
using Application.Channels.Services;
using Application.Gossip;
using Application.Gossip.Interfaces;
using Application.Payments;
using Application.Payments.Routing;
using Application.Payments.Switch;
using Application.Protocol.Factories;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Hashes;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.Options;
using Domain.Onchain.Interfaces;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Payments.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Protocol.Onion;
using Infrastructure.Repositories;
using Infrastructure.Serialization;

/// <summary>
/// Three in-process nodes Alice → Bob → Carol (ABCD W2-B proof): each is a real <see cref="ChannelManager"/> with the
/// production normal-operation handlers, engine ports, <c>LocalLightningSigner</c>, real Sphinx and error onions,
/// the production <see cref="HtlcSwitch"/> and its own SQLite database (real migrations and repositories), so a node
/// can be stopped and started again on the same keys and database.
/// </summary>
/// <remarks>
/// <para>Channels: Alice–Bob (Alice funds, pushes 800,000 sat) and Bob–Carol (Bob funds), both open with a real
/// <c>short_channel_id</c>, their first commitments persisted, and a signed <c>channel_update</c> made by each side.
/// Messages go through per-direction FIFOs that <see cref="PumpAsync"/> delivers round-robin (so they cross like on
/// real links) until every queue is empty and every commit scheduler is idle.</para>
/// <para>Hooks for the proofs: every node's switch can be suspended (events are recorded but not handled, as if the
/// node crashed right after the persist that raised them); every message a node sends is logged with the sender's
/// channel states at that moment; every node's channel lock provider records a flow that takes a second channel lock
/// while holding one. <see cref="RestartAsync"/> stops a node (the messages it had queued are lost, its peers see it
/// as disconnected) and starts it again from its database: channels reloaded and registered (startup replay while
/// no link is up), then <see cref="ReconnectAsync"/> stands in for <c>channel_reestablish</c> (N7): the links are
/// marked up through the production (decorated) <see cref="IPeerLivenessProbe"/>, whose
/// <see cref="LinkUpEventReplayer"/> replays the pending events, as in the daemon. <see cref="Disconnect"/> and
/// <see cref="ReconnectLinkAsync"/> do the same for one link without a restart. Restarts and reconnections are only
/// meant at a quiescent point, since nothing retransmits.</para>
/// </remarks>
[ExcludeFromCodeCoverage]
internal sealed class ThreeNodeHarness : IAsyncDisposable
{
    public const ulong FundingSatoshis = 2_000_000;
    public const ulong PushSatoshis = 800_000;
    public const uint InitialFeeratePerKw = 2_500;
    public const uint BlockHeight = 500;

    public static readonly ChannelId AliceBobChannelId = new(Enumerable.Repeat((byte)0xAB, 32).ToArray());
    public static readonly ChannelId BobCarolChannelId = new(Enumerable.Repeat((byte)0xBC, 32).ToArray());
    public static readonly ShortChannelId AliceBobScid = new(400, 1, 0);
    public static readonly ShortChannelId BobCarolScid = new(400, 2, 1);

    /// <summary>Bob's forwarding policy (distinct values so a fee mix-up shows).</summary>
    public static RoutingOptions BobRouting => new()
    {
        FeeBaseMsat = 1_000,
        FeeProportionalMillionths = 100,
        CltvExpiryDelta = 40
    };

    private readonly string _directory;
    private readonly ConcurrentDictionary<(string From, string To), ConcurrentQueue<IChannelMessage>> _links = new();

    public SwitchNode Alice { get; }
    public SwitchNode Bob { get; }
    public SwitchNode Carol { get; }
    public IReadOnlyList<SwitchNode> Nodes => [Alice, Bob, Carol];

    /// <summary>Every message delivered, in delivery order.</summary>
    public List<SentMessage> Sent { get; } = [];

    private ThreeNodeHarness(string directory)
    {
        _directory = directory;
        Alice = new SwitchNode(this, "Alice", 0xA1, Path.Combine(directory, "alice.db"), new RoutingOptions());
        Bob = new SwitchNode(this, "Bob", 0xB0, Path.Combine(directory, "bob.db"), BobRouting);
        Carol = new SwitchNode(this, "Carol", 0xC0, Path.Combine(directory, "carol.db"), new RoutingOptions());
    }

    /// <param name="beforeStart">Runs before the nodes start, e.g. to set <see cref="SwitchNode.ConfigureServices"/>.
    /// </param>
    public static async Task<ThreeNodeHarness> CreateAsync(Action<ThreeNodeHarness>? beforeStart = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"nltg-three-node-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var harness = new ThreeNodeHarness(directory);
        beforeStart?.Invoke(harness);
        foreach (var node in harness.Nodes)
            await node.StartAsync(migrate: true);

        await harness.OpenChannelAsync(harness.Alice, 1, harness.Bob, 1, AliceBobChannelId, AliceBobScid, 0x71);
        await harness.OpenChannelAsync(harness.Bob, 2, harness.Carol, 1, BobCarolChannelId, BobCarolScid, 0x72);
        return harness;
    }

    /// <summary>
    /// Delivers queued messages, one per link in turn, until every queue is empty and no commit scheduler has a
    /// signature waiting.
    /// </summary>
    public async Task PumpAsync()
    {
        await WhenReplaysIdleAsync();
        for (var steps = 0; steps < 20_000; steps++)
        {
            await WhenSchedulersIdleAsync();
            var delivered = false;
            foreach (var key in _links.Keys.OrderBy(k => k.From).ThenBy(k => k.To).ToList())
                delivered |= await DeliverNextAsync(key);

            if (delivered)
                continue;

            await WhenSchedulersIdleAsync();
            if (_links.Values.All(q => q.IsEmpty))
                return;
        }

        throw new InvalidOperationException("The message exchange did not converge");
    }

    /// <summary>
    /// Stops <paramref name="node"/> (queued messages to and from it are lost; its peers see it disconnected), then
    /// starts it again from its database and registers every stored channel (the startup replay runs while no link
    /// is up). Call <see cref="ReconnectAsync"/> next.
    /// </summary>
    public async Task RestartAsync(SwitchNode node)
    {
        await WhenReplaysIdleAsync();
        await WhenSchedulersIdleAsync();
        foreach (var peer in Nodes.Where(n => n != node))
        {
            peer.SetPeerAlive(node.NodeId, false);
            _links.TryRemove((peer.Name, node.Name), out _);
            _links.TryRemove((node.Name, peer.Name), out _);
        }

        await node.StopAsync();
        await node.StartAsync(migrate: false);
        foreach (var peer in Nodes.Where(n => n != node))
            node.SetPeerAlive(peer.NodeId, false);

        await node.LoadStoredChannelsAsync();
    }

    /// <summary>
    /// Stands in for <c>channel_reestablish</c> (BOLT2 N7) at a quiescent point: both ends of each of
    /// <paramref name="node"/>'s channels mark their link up again, then <paramref name="node"/> replays its pending
    /// events (<see cref="ChannelDomainEvents.DerivePending(ChannelCommitments, IEnumerable{HtlcRecord})"/>) into its
    /// switch, as N7 will.
    /// </summary>
    public async Task ReconnectAsync(SwitchNode node)
    {
        // Every link is up before any replay runs (as when N7 has reestablished all of the node's channels), so a
        // replayed lock-in can be forwarded; then the production MarkLinkUp replays each channel
        foreach (var peer in Nodes.Where(n => n != node))
        {
            peer.SetPeerAlive(node.NodeId, true);
            node.SetPeerAlive(peer.NodeId, true);
        }

        foreach (var channel in node.Channels)
        {
            node.Probe.MarkLinkUp(channel.ChannelId, channel.RemoteNodeId);
            Find(channel.RemoteNodeId).Probe.MarkLinkUp(channel.ChannelId, node.NodeId);
        }

        foreach (var peer in Nodes.Where(n => n != node))
            await ReconnectLinkAsync(node, peer);
    }

    /// <summary>
    /// The link between <paramref name="a"/> and <paramref name="b"/> drops without a restart: each side sees the
    /// other disconnected and every channel between them is down until marked up again (as
    /// <c>ConnectedPeerLivenessProbe</c> after a reconnection).
    /// </summary>
    public void Disconnect(SwitchNode a, SwitchNode b)
    {
        a.SetPeerAlive(b.NodeId, false);
        b.SetPeerAlive(a.NodeId, false);
        foreach (var channel in a.Channels.Where(c => c.RemoteNodeId == b.NodeId))
        {
            a.Probe.MarkLinkDown(channel.ChannelId);
            b.Probe.MarkLinkDown(channel.ChannelId);
        }
    }

    /// <summary>
    /// Stands in for <c>channel_reestablish</c> (N7) between <paramref name="a"/> and <paramref name="b"/>: both see
    /// each other again and both ends of every channel between them call the production
    /// <see cref="IPeerLivenessProbe.MarkLinkUp"/> (nothing else); waits for the replays it triggers.
    /// </summary>
    public async Task ReconnectLinkAsync(SwitchNode a, SwitchNode b)
    {
        a.SetPeerAlive(b.NodeId, true);
        b.SetPeerAlive(a.NodeId, true);
        foreach (var channel in a.Channels.Where(c => c.RemoteNodeId == b.NodeId))
        {
            a.MarkLinkUp(channel.ChannelId, b.NodeId);
            b.MarkLinkUp(channel.ChannelId, a.NodeId);
        }

        await WhenReplaysIdleAsync();
    }

    /// <summary>
    /// The route Alice → Bob → Carol for <paramref name="amount"/> with Bob's fee and CLTV delta, the payee's final
    /// CLTV at height + <paramref name="finalCltvDelta"/>. <paramref name="payee"/> replaces Carol's node id in the
    /// onion (to make Carol's peel fail).
    /// </summary>
    public PaymentRoute RouteToCarol(LightningMoney amount, Hash paymentHash, Secret paymentSecret,
                                     uint finalCltvDelta = 43, CompactPubKey? payee = null)
    {
        var routing = BobRouting;
        var finalCltv = BlockHeight + finalCltvDelta;
        var bobFee = ForwardingFeeOf(routing, amount);
        var hops = new List<RouteHop>
        {
            new(Bob.NodeId, amount, finalCltv, BobCarolScid),
            new(payee ?? Carol.NodeId, amount, finalCltv, null)
        };
        return new PaymentRoute(hops, amount + bobFee, finalCltv + routing.CltvExpiryDelta, paymentHash,
                                paymentSecret);
    }

    public static LightningMoney ForwardingFeeOf(RoutingOptions routing, LightningMoney amount) =>
        LightningMoney.MilliSatoshis(routing.FeeBaseMsat
                                   + amount.MilliSatoshi * routing.FeeProportionalMillionths / 1_000_000);

    /// <summary>Alice builds the onion for <paramref name="route"/> and offers the first HTLC to Bob.</summary>
    public async Task<(ulong HtlcId, PaymentOnion Onion)> AlicePaysAsync(PaymentRoute route)
    {
        var onion = await Alice.Services.GetRequiredService<PaymentOnionFactory>().CreateAsync(route);
        var htlcId = await Alice.Operations.OfferHtlcAsync(AliceBobChannelId, route.FirstHopAmount,
                                                           route.PaymentHash, route.FirstHopCltvExpiry, onion.Packet,
                                                           null, HtlcOrigin.Local(route.PaymentHash));
        return (htlcId, onion);
    }

    public SwitchNode Find(CompactPubKey nodeId) =>
        Nodes.Single(n => n.NodeId == nodeId);

    public static Hash Sha256Of(ReadOnlySpan<byte> bytes) => new(SHA256.HashData(bytes));

    public async ValueTask DisposeAsync()
    {
        foreach (var node in Nodes)
            await node.StopAsync();

        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // Best effort: a file still held by the OS is left in the temp folder
        }
    }

    internal void Route(SwitchNode from, CompactPubKey to, IChannelMessage message)
    {
        var target = Find(to);
        if (!from.IsPeerAlive(to) || !target.IsRunning)
        {
            from.Dropped.Add(message);
            return;
        }

        _links.GetOrAdd((from.Name, target.Name), _ => new ConcurrentQueue<IChannelMessage>()).Enqueue(message);
        lock (Sent)
            Sent.Add(new SentMessage(from.Name, target.Name, message, from.SnapshotCommitments()));
    }

    private async Task<bool> DeliverNextAsync((string From, string To) key)
    {
        if (!_links.TryGetValue(key, out var queue) || !queue.TryDequeue(out var message))
            return false;

        var from = Nodes.Single(n => n.Name == key.From);
        var to = Nodes.Single(n => n.Name == key.To);
        to.Received.Add(message);

        // One flow per delivery: the lock audit follows it into the switch and the channel operations it calls
        LockAudit.BeginFlow();
        await to.ChannelManager.HandleChannelMessageAsync(message, new FeatureOptions(), from.NodeId);
        return true;
    }

    private async Task WhenSchedulersIdleAsync()
    {
        foreach (var node in Nodes.Where(n => n.IsRunning))
            await node.Scheduler.WhenIdleAsync();
    }

    private async Task WhenReplaysIdleAsync()
    {
        foreach (var node in Nodes.Where(n => n.IsRunning))
            await node.Replayer.WhenIdleAsync();
    }

    private async Task OpenChannelAsync(SwitchNode funder, uint funderKeyIndex, SwitchNode fundee, uint fundeeKeyIndex,
                                        ChannelId channelId, ShortChannelId scid, byte fundingTag)
    {
        var funderParty = new ChannelParty(LightningMoney.Satoshis(546), LightningMoney.Satoshis(20_000),
                                           LightningMoney.MilliSatoshis(1_000), 30,
                                           LightningMoney.Satoshis(FundingSatoshis), 144);
        var fundeeParty = new ChannelParty(LightningMoney.Satoshis(600), LightningMoney.Satoshis(20_000),
                                           LightningMoney.MilliSatoshis(1_000), 30,
                                           LightningMoney.Satoshis(FundingSatoshis), 100);
        var fundingTxId = new TxId(Enumerable.Repeat(fundingTag, 32).ToArray());
        var funderBasepoints = funder.Basepoints(funderKeyIndex);
        var fundeeBasepoints = fundee.Basepoints(fundeeKeyIndex);
        var obscuring = new CommitmentNumber(funderBasepoints.PaymentBasepoint, fundeeBasepoints.PaymentBasepoint,
                                             new Sha256());

        var funderChannel = CreateChannel(funder, funderKeyIndex, fundee, fundeeKeyIndex, funderParty, fundeeParty,
                                          true, channelId, scid, fundingTxId, obscuring);
        var fundeeChannel = CreateChannel(fundee, fundeeKeyIndex, funder, funderKeyIndex, fundeeParty, funderParty,
                                          false, channelId, scid, fundingTxId, obscuring);

        await funder.OpenAsync(funderChannel, fundee.Point(fundeeKeyIndex, 0), fundee.Point(fundeeKeyIndex, 1));
        await fundee.OpenAsync(fundeeChannel, funder.Point(funderKeyIndex, 0), funder.Point(funderKeyIndex, 1));
    }

    private static ChannelModel CreateChannel(SwitchNode self, uint selfKeyIndex, SwitchNode peer, uint peerKeyIndex,
                                              ChannelParty local, ChannelParty remote, bool isInitiator,
                                              ChannelId channelId, ShortChannelId scid, TxId fundingTxId,
                                              CommitmentNumber obscuring)
    {
        var selfBasepoints = self.Basepoints(selfKeyIndex);
        var peerBasepoints = peer.Basepoints(peerKeyIndex);
        var channelParams = new ChannelParams(local, remote, LightningMoney.Satoshis(InitialFeeratePerKw), 3, false,
                                              FeatureSupport.No);
        var funderKey = isInitiator ? selfBasepoints.FundingPubKey : peerBasepoints.FundingPubKey;
        var fundeeKey = isInitiator ? peerBasepoints.FundingPubKey : selfBasepoints.FundingPubKey;
        var fundingOutput = new FundingOutputInfo(LightningMoney.Satoshis(FundingSatoshis), funderKey, fundeeKey,
                                                  fundingTxId, 0);
        var localKeySet = new ChannelKeySetModel(selfKeyIndex, selfBasepoints.FundingPubKey,
                                                 selfBasepoints.RevocationBasepoint, selfBasepoints.PaymentBasepoint,
                                                 selfBasepoints.DelayedPaymentBasepoint, selfBasepoints.HtlcBasepoint,
                                                 self.Point(selfKeyIndex, 0));
        var remoteKeySet = new ChannelKeySetModel(0, peerBasepoints.FundingPubKey, peerBasepoints.RevocationBasepoint,
                                                  peerBasepoints.PaymentBasepoint,
                                                  peerBasepoints.DelayedPaymentBasepoint,
                                                  peerBasepoints.HtlcBasepoint, peer.Point(peerKeyIndex, 0));
        var localSat = isInitiator ? FundingSatoshis - PushSatoshis : PushSatoshis;
        return new ChannelModel(channelParams, channelId, obscuring, fundingOutput, isInitiator, null, null,
                                LightningMoney.Satoshis(localSat), localKeySet, 0, 0,
                                LightningMoney.Satoshis(FundingSatoshis - localSat), remoteKeySet, 0, peer.NodeId, 0,
                                ChannelState.Open, ChannelVersion.V1)
        {
            ShortChannelId = scid
        };
    }
}

/// <summary>A message one node sent another, with the sender's channel states when it was sent.</summary>
internal sealed record SentMessage(
    string From,
    string To,
    IChannelMessage Message,
    IReadOnlyDictionary<ChannelId, ChannelCommitments> SenderStates);

/// <summary>One node of <see cref="ThreeNodeHarness"/>.</summary>
[ExcludeFromCodeCoverage]
internal sealed class SwitchNode
{
    private readonly ThreeNodeHarness _harness;
    private readonly ConcurrentDictionary<CompactPubKey, bool> _peerAlive = new();
    private ServiceProvider? _provider;

    public string Name { get; }
    public string DatabasePath { get; }
    public HarnessKeyManager KeyManager { get; }
    public CompactPubKey NodeId => KeyManager.NodeId;
    public NodeOptions Options { get; }
    public LockAudit LockAudit { get; } = new();
    public LinkProbe Probe { get; }
    public RecordingPaymentHandler PaymentHandler { get; } = new();

    /// <summary>
    /// While true the switch records events without handling them, as if the node stopped right after the persist
    /// that raised them.
    /// </summary>
    public bool SwitchSuspended { get; set; }

    /// <summary>Every event handed to the switch (handled or suspended), in order, across restarts.</summary>
    public List<IChannelDomainEvent> Events { get; } = [];

    /// <summary>Messages received, in order, across restarts.</summary>
    public List<IChannelMessage> Received { get; } = [];

    /// <summary>Messages raised for a peer that was away (lost, like in production).</summary>
    public List<IChannelMessage> Dropped { get; } = [];

    /// <summary>Called with every message the node publishes, under the channel's lock, right after the save that
    /// produced it (before it is routed).</summary>
    public Action<IChannelMessage>? OnPublish { get; set; }

    /// <summary>Called after every invoice read through a unit of work (<c>IInvoiceDbRepository.GetByPaymentHashAsync</c>),
    /// before the caller gets the result; its task delays the caller.</summary>
    public Func<Hash, Task>? AfterInvoiceRead { get; set; }

    /// <summary>Called before every <c>IChannelStateDbRepository.SetHtlcOriginAsync</c> (staged with an offer's add);
    /// throwing from it fails the offer before its save.</summary>
    public Action<HtlcOrigin>? BeforeSetHtlcOrigin { get; set; }

    /// <summary>Last changes to the node's services, applied on every start (e.g. a manual clock).</summary>
    public Action<IServiceCollection>? ConfigureServices { get; set; }

    public bool IsRunning => _provider is not null;
    public IServiceProvider Services => _provider ?? throw new InvalidOperationException($"{Name} is stopped");
    public ChannelManager ChannelManager { get; private set; } = null!;
    public IChannelOperations Operations => Services.GetRequiredService<IChannelOperations>();
    public ICommitScheduler Scheduler => Services.GetRequiredService<ICommitScheduler>();
    public IHtlcSwitch Switch => Services.GetRequiredService<IHtlcSwitch>();
    public IInvoiceService Invoices => Services.GetRequiredService<IInvoiceService>();
    public IChannelMemoryRepository Memory => Services.GetRequiredService<IChannelMemoryRepository>();
    public ILightningSigner Signer => Services.GetRequiredService<ILightningSigner>();
    public LinkUpEventReplayer Replayer => Services.GetRequiredService<LinkUpEventReplayer>();

    public IReadOnlyList<ChannelModel> Channels => Memory.FindChannels(_ => true);

    public SwitchNode(ThreeNodeHarness harness, string name, byte seed, string databasePath, RoutingOptions routing)
    {
        _harness = harness;
        Name = name;
        DatabasePath = databasePath;
        KeyManager = new HarnessKeyManager(seed);
        Options = new NodeOptions
        {
            BitcoinNetwork = BitcoinNetwork.Regtest,
            EnableHtlcs = true,
            Routing = routing
        };
        Probe = new LinkProbe(this);
    }

    public ChannelModel Channel(ChannelId channelId) =>
        Memory.TryGetChannel(channelId, out var channel) ? channel : throw new InvalidOperationException("No channel");

    public ChannelBasepoints Basepoints(uint keyIndex) => Signer.GetChannelBasepoints(keyIndex);

    public CompactPubKey Point(uint keyIndex, ulong commitmentNumber) =>
        Signer.GetPerCommitmentPoint(keyIndex, commitmentNumber);

    public bool IsPeerAlive(CompactPubKey peer) => _peerAlive.GetValueOrDefault(peer, true);

    public void SetPeerAlive(CompactPubKey peer, bool alive) => _peerAlive[peer] = alive;

    /// <summary>Marks the link up through the production (decorated) probe, which replays the channel's pending events.
    /// </summary>
    public void MarkLinkUp(ChannelId channelId, CompactPubKey peer) =>
        Services.GetRequiredService<IPeerLivenessProbe>().MarkLinkUp(channelId, peer);

    public IReadOnlyDictionary<ChannelId, ChannelCommitments> SnapshotCommitments() =>
        Channels.Where(c => c.Commitments is not null).ToDictionary(c => c.ChannelId, c => c.Commitments!);

    /// <summary>Runs <paramref name="action"/> with a fresh scope's unit of work.</summary>
    public async Task<T> InScopeAsync<T>(Func<IUnitOfWork, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
    }

    public async Task StartAsync(bool migrate)
    {
        _provider = BuildProvider();
        if (migrate)
        {
            using var scope = _provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<NLightningDbContext>().Database.MigrateAsync();
        }

        ChannelManager = _provider.GetRequiredService<ChannelManager>();
        ChannelManager.OnResponseMessageReady += (_, args) => _harness.Route(this, args.PeerPubKey,
                                                                             args.ResponseMessage);
    }

    public async Task StopAsync()
    {
        if (_provider is null)
            return;

        await Replayer.WhenIdleAsync();
        await Scheduler.WhenIdleAsync();
        var provider = _provider;
        _provider = null;
        await provider.DisposeAsync();
        Probe.Clear();
    }

    /// <summary>Persists a newly opened channel with its first commitments and registers it (as channel_ready).</summary>
    public async Task OpenAsync(ChannelModel channel, CompactPubKey peerPoint0, CompactPubKey peerPoint1)
    {
        channel.UpdateCommitments(ChannelStateTransitionService.CreateInitialCommitments(channel, peerPoint0,
                                                                                         peerPoint1));
        await InScopeAsync(async unitOfWork =>
        {
            await unitOfWork.ChannelDbRepository.AddAsync(channel);
            await unitOfWork.ChannelStateDbRepository.InitializeAsync(channel.Commitments!);
            await unitOfWork.SaveChangesAsync();
            return true;
        });

        Signer.RegisterChannel(channel.ChannelId, channel.GetSigningInfo());
        Memory.AddChannel(channel);
        MarkLinkUp(channel.ChannelId, channel.RemoteNodeId);

        // Our signed channel_update, as the gossip service makes it once the channel is open
        Services.GetRequiredService<IChannelUpdateService>().CreateChannelUpdate(channel);
    }

    /// <summary>Loads every stored channel and registers it with the channel manager (startup, no link up).</summary>
    public async Task LoadStoredChannelsAsync()
    {
        var channels = await InScopeAsync(async unitOfWork =>
                                              (await unitOfWork.ChannelDbRepository.GetAllAsync()).ToList());
        foreach (var channel in channels)
        {
            LockAudit.BeginFlow();
            await ChannelManager.RegisterExistingChannelAsync(channel);
            if (channel.State == ChannelState.Open)
                Services.GetRequiredService<IChannelUpdateService>().CreateChannelUpdate(channel);
        }
    }

    /// <summary>The events still pending in every channel, handed to the switch (what N7 does after a reestablish).</summary>
    public async Task ReplayPendingEventsAsync()
    {
        foreach (var channel in Channels.Where(c => c.Commitments is not null))
        {
            var persisted = await InScopeAsync(unitOfWork => unitOfWork.ChannelStateDbRepository.LoadAsync(
                                                   channel.ChannelId, channel.Commitments!.Params));
            foreach (var channelEvent in ChannelDomainEvents.DerivePending(channel.Commitments!,
                                                                          persisted?.SettledHtlcs))
            {
                LockAudit.BeginFlow();
                await Switch.HandleAsync(channelEvent, CancellationToken.None);
            }
        }
    }

    private ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder()
                           .AddInMemoryCollection(new Dictionary<string, string?>
                           {
                               ["Database:Provider"] = "sqlite",
                               ["Database:ConnectionString"] = $"Data Source={DatabasePath}"
                           })
                           .Build();

        var blockchainMonitor = new Mock<IBlockchainMonitor>();
        blockchainMonitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(ThreeNodeHarness.BlockHeight);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(Options));
        services.AddSingleton<ISecureKeyManager>(KeyManager);
        services.AddSingleton<IOnionReplayCache>(new OnionReplayCache());
        services.AddTransient<ISha256, Sha256>();
        services.AddSerializationInfrastructureServices();
        services.AddBitcoinInfrastructure();
        services.AddPersistenceInfrastructureServices(configuration);
        services.AddRepositoriesInfrastructureServices();
        services.Replace(ServiceDescriptor.Scoped<IUnitOfWork>(
                             sp => new HookedUnitOfWork(ActivatorUtilities.CreateInstance<UnitOfWork>(sp), this)));
        services.AddSingleton(blockchainMonitor.Object);
        services.AddSingleton<ICommitmentTransactionModelFactory, CommitmentTransactionModelFactory>();
        services.AddCommitmentEngineServices();
        services.AddChannelStateTransitionServices();
        services.AddSingleton<IMessageFactory, MessageFactory>();
        services.AddSingleton<IChannelLockProvider>(LockAudit);
        services.AddSingleton<IChannelMessagePublisher>(new LazyPublisher(this));
        services.AddSingleton<IPeerLivenessProbe>(Probe);
        services.AddChannelOperationsServices();
        services.Configure<CommitSchedulerOptions>(o => o.Debounce = TimeSpan.Zero);
        services.AddGossipServices();
        services.AddPaymentsServices();
        services.AddHtlcSwitchServices();
        services.AddSingleton<ILocalPaymentHtlcHandler>(PaymentHandler);
        services.AddSingleton<HtlcSwitch>();
        services.Replace(ServiceDescriptor.Singleton<IHtlcSwitch>(
                             sp => new GatedSwitch(this, sp.GetRequiredService<HtlcSwitch>())));
        services.AddSingleton(sp => new ChannelManager(new Mock<IBlockchainMonitor>().Object,
                                                       sp.GetRequiredService<IChannelLockProvider>(),
                                                       sp.GetRequiredService<IChannelMemoryRepository>(),
                                                       Microsoft.Extensions.Logging.Abstractions.NullLogger<
                                                           ChannelManager>.Instance,
                                                       sp.GetRequiredService<ILightningSigner>(), sp));
        services.AddScoped<IChannelMessageHandler<UpdateAddHtlcMessage>, UpdateAddHtlcMessageHandler>();
        services.AddScoped<IChannelMessageHandler<UpdateFulfillHtlcMessage>, UpdateFulfillHtlcMessageHandler>();
        services.AddScoped<IChannelMessageHandler<UpdateFailHtlcMessage>, UpdateFailHtlcMessageHandler>();
        services
           .AddScoped<IChannelMessageHandler<UpdateFailMalformedHtlcMessage>, UpdateFailMalformedHtlcMessageHandler>();
        services.AddScoped<IChannelMessageHandler<CommitmentSignedMessage>, CommitmentSignedMessageHandler>();
        services.AddScoped<IChannelMessageHandler<RevokeAndAckMessage>, RevokeAndAckMessageHandler>();
        services.AddScoped<IChannelMessageHandler<UpdateFeeMessage>, UpdateFeeMessageHandler>();
        ConfigureServices?.Invoke(services);
        return services.BuildServiceProvider();
    }

    /// <summary>Publishes through the node's channel manager, which is built after the provider.</summary>
    private sealed class LazyPublisher(SwitchNode node) : IChannelMessagePublisher
    {
        public void Publish(CompactPubKey peerPubKey, IReadOnlyList<IChannelMessage> messages)
        {
            foreach (var message in messages)
                node.OnPublish?.Invoke(message);
            node.ChannelManager.Publish(peerPubKey, messages);
        }
    }

    /// <summary>Records every event and hands it to the production switch unless the switch is suspended.</summary>
    private sealed class GatedSwitch(SwitchNode node, IHtlcSwitch inner) : IHtlcSwitch
    {
        public async Task HandleAsync(IChannelDomainEvent channelEvent, CancellationToken cancellationToken)
        {
            lock (node.Events)
                node.Events.Add(channelEvent);
            if (!node.SwitchSuspended)
                await inner.HandleAsync(channelEvent, cancellationToken);
        }
    }
}

/// <summary>
/// The channel's link for the send side: up once marked (channel opened or reestablished) and while the peer is
/// alive; a restart clears every mark.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class LinkProbe(SwitchNode node) : IPeerLivenessProbe
{
    private readonly ConcurrentDictionary<ChannelId, byte> _links = new();

    public Task<bool> IsAliveAsync(ChannelId channelId, CompactPubKey peerPubKey,
                                   CancellationToken cancellationToken = default) =>
        Task.FromResult(node.IsPeerAlive(peerPubKey) && _links.ContainsKey(channelId));

    public void MarkLinkUp(ChannelId channelId, CompactPubKey peerPubKey) => _links[channelId] = 0;

    public void MarkLinkDown(ChannelId channelId) => _links.TryRemove(channelId, out _);

    public void Clear() => _links.Clear();
}

/// <summary>
/// The production <see cref="UnitOfWork"/> with the invoice reads hooked (<see cref="SwitchNode.AfterInvoiceRead"/>).
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class HookedUnitOfWork(IUnitOfWork inner, SwitchNode node) : IUnitOfWork
{
    public IBlockchainStateDbRepository BlockchainStateDbRepository => inner.BlockchainStateDbRepository;
    public IWatchedTransactionDbRepository WatchedTransactionDbRepository => inner.WatchedTransactionDbRepository;
    public IWalletAddressesDbRepository WalletAddressesDbRepository => inner.WalletAddressesDbRepository;
    public IUtxoDbRepository UtxoDbRepository => inner.UtxoDbRepository;
    public IWatchedOutpointDbRepository WatchedOutpointDbRepository => inner.WatchedOutpointDbRepository;
    public IBroadcastTransactionDbRepository BroadcastTransactionDbRepository =>
        inner.BroadcastTransactionDbRepository;
    public IBlockHeaderDbRepository BlockHeaderDbRepository => inner.BlockHeaderDbRepository;
    public IRevokedCommitmentDbRepository RevokedCommitmentDbRepository => inner.RevokedCommitmentDbRepository;
    public IOnchainResolutionDbRepository OnchainResolutionDbRepository => inner.OnchainResolutionDbRepository;
    public IChannelConfigDbRepository ChannelConfigDbRepository => inner.ChannelConfigDbRepository;
    public IChannelDbRepository ChannelDbRepository => inner.ChannelDbRepository;
    public IChannelKeySetDbRepository ChannelKeySetDbRepository => inner.ChannelKeySetDbRepository;
    public IChannelStateDbRepository ChannelStateDbRepository =>
        new HookedChannelStateRepository(inner.ChannelStateDbRepository, node);
    public IRemoteShachainDbRepository RemoteShachainDbRepository => inner.RemoteShachainDbRepository;
    public IPeerDbRepository PeerDbRepository => inner.PeerDbRepository;
    public IInvoiceDbRepository InvoiceDbRepository => new HookedInvoiceRepository(inner.InvoiceDbRepository, node);
    public IPaymentDbRepository PaymentDbRepository => inner.PaymentDbRepository;
    public IForwardCircuitDbRepository ForwardCircuitDbRepository => inner.ForwardCircuitDbRepository;

    public Task<ICollection<PeerModel>> GetPeersForStartupAsync() => inner.GetPeersForStartupAsync();
    public void AddUtxo(UtxoModel utxoModel) => inner.AddUtxo(utxoModel);
    public void TrySpendUtxo(TxId transactionId, uint index) => inner.TrySpendUtxo(transactionId, index);
    public void SaveChanges() => inner.SaveChanges();
    public Task SaveChangesAsync() => inner.SaveChangesAsync();
    public void Dispose() => inner.Dispose();

    private sealed class HookedChannelStateRepository(IChannelStateDbRepository inner, SwitchNode node)
        : IChannelStateDbRepository
    {
        public Task InitializeAsync(ChannelCommitments snapshot, ChannelStateExtras? extras = null) =>
            inner.InitializeAsync(snapshot, extras);

        public Task ApplyAsync(ChannelCommitments next, ChannelTransition transition,
                               ChannelStateExtras? extras = null) =>
            inner.ApplyAsync(next, transition, extras);

        public Task<PersistedChannelState?> LoadAsync(ChannelId channelId, CommitmentParams @params) =>
            inner.LoadAsync(channelId, @params);

        public Task SetOnionSharedSecretAsync(ChannelId channelId, HtlcKey htlc, Secret sharedSecret) =>
            inner.SetOnionSharedSecretAsync(channelId, htlc, sharedSecret);

        public Task<Secret?> GetOnionSharedSecretAsync(ChannelId channelId, HtlcKey htlc) =>
            inner.GetOnionSharedSecretAsync(channelId, htlc);

        public Task SetHtlcOriginAsync(ChannelId channelId, HtlcKey htlc, HtlcOrigin origin)
        {
            node.BeforeSetHtlcOrigin?.Invoke(origin);
            return inner.SetHtlcOriginAsync(channelId, htlc, origin);
        }

        public Task<HtlcOrigin?> GetHtlcOriginAsync(ChannelId channelId, HtlcKey htlc) =>
            inner.GetHtlcOriginAsync(channelId, htlc);

        public Task<IReadOnlyList<(ChannelId ChannelId, HtlcKey Htlc)>> FindHtlcsByOriginAsync(HtlcOrigin origin) =>
            inner.FindHtlcsByOriginAsync(origin);

        public Task PruneSettledHtlcsAsync(ChannelId channelId, IEnumerable<HtlcKey> htlcs) =>
            inner.PruneSettledHtlcsAsync(channelId, htlcs);
    }

    private sealed class HookedInvoiceRepository(IInvoiceDbRepository inner, SwitchNode node) : IInvoiceDbRepository
    {
        public Task AddAsync(InvoiceModel invoice) => inner.AddAsync(invoice);
        public Task UpdateAsync(InvoiceModel invoice) => inner.UpdateAsync(invoice);

        public async Task<InvoiceModel?> GetByPaymentHashAsync(Hash paymentHash)
        {
            var invoice = await inner.GetByPaymentHashAsync(paymentHash);
            if (node.AfterInvoiceRead is { } hook)
                await hook(paymentHash);
            return invoice;
        }

        public Task<IReadOnlyList<InvoiceModel>> ListAsync(int skip, int take) => inner.ListAsync(skip, take);
    }
}

/// <summary>
/// A <see cref="ChannelLockProvider"/> that records any flow taking a second channel lock while it holds one
/// ("never two channel locks"). A flow is one message delivery, one replay step, or one commit-scheduler round
/// (<see cref="BeginFlow"/> starts a flow for the caller's async context).
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class LockAudit : IChannelLockProvider
{
    private static readonly AsyncLocal<Holder?> s_holder = new();
    private readonly ChannelLockProvider _inner = new();

    public List<string> Violations { get; } = [];
    public int MaxHeldInOneFlow { get; private set; }

    /// <summary>Starts a new flow in the caller's async context.</summary>
    public static void BeginFlow() => s_holder.Value = new Holder();

    public Task<IDisposable> AcquireAsync(ChannelId channelId, CancellationToken cancellationToken = default)
    {
        var holder = s_holder.Value ??= new Holder();
        return TrackAsync(holder, channelId, _inner.AcquireAsync(channelId, cancellationToken));
    }

    public IDisposable Acquire(ChannelId channelId)
    {
        var holder = s_holder.Value ??= new Holder();
        return Track(holder, channelId, _inner.Acquire(channelId));
    }

    private async Task<IDisposable> TrackAsync(Holder holder, ChannelId channelId, Task<IDisposable> acquiring) =>
        Track(holder, channelId, await acquiring);

    private IDisposable Track(Holder holder, ChannelId channelId, IDisposable channelLock)
    {
        lock (holder)
        {
            if (holder.Held.Count > 0)
                lock (Violations)
                    Violations.Add($"{channelId} taken while holding {string.Join(", ", holder.Held)}");
            holder.Held.Add(channelId);
            MaxHeldInOneFlow = Math.Max(MaxHeldInOneFlow, holder.Held.Count);
        }

        return new Tracked(holder, channelId, channelLock);
    }

    private sealed class Holder
    {
        public List<ChannelId> Held { get; } = [];
    }

    private sealed class Tracked(Holder holder, ChannelId channelId, IDisposable inner) : IDisposable
    {
        public void Dispose()
        {
            lock (holder)
                holder.Held.Remove(channelId);
            inner.Dispose();
        }
    }
}

/// <summary>Records the resolutions of our own payments (<c>HtlcOrigin.Local</c>).</summary>
[ExcludeFromCodeCoverage]
internal sealed class RecordingPaymentHandler : ILocalPaymentHtlcHandler
{
    public List<OutgoingHtlcFulfilled> Fulfilled { get; } = [];
    public List<OutgoingHtlcFailed> Failed { get; } = [];

    public Task HandleFulfilledAsync(OutgoingHtlcFulfilled fulfilled, Hash paymentHash,
                                     CancellationToken cancellationToken)
    {
        lock (Fulfilled)
            Fulfilled.Add(fulfilled);
        return Task.CompletedTask;
    }

    public Task HandleFailedAsync(OutgoingHtlcFailed failed, Hash paymentHash, CancellationToken cancellationToken)
    {
        lock (Failed)
            Failed.Add(failed);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Node key (ECDH for Sphinx, node signatures) and channel keys (BIP32 from the same seed) of a harness node.
/// </summary>
/// <remarks>Hand-written because Moq cannot set up methods with span parameters.</remarks>
[ExcludeFromCodeCoverage]
internal sealed class HarnessKeyManager : ISecureKeyManager
{
    private readonly Key _nodeKey;
    private readonly ExtKey _channelRoot;

    public HarnessKeyManager(byte seed)
    {
        _nodeKey = new Key(Enumerable.Repeat(seed, 32).ToArray());
        _channelRoot = new ExtKey(new Key(Enumerable.Repeat((byte)(seed ^ 0x5A), 32).ToArray()), new byte[32]);
    }

    public CompactPubKey NodeId => new(_nodeKey.PubKey.ToBytes());

    public BitcoinKeyPath ChannelKeyPath => throw new NotSupportedException();
    public uint HeightOfBirth => 0;

    public ExtPrivKey GetNextChannelKey(out uint index) => throw new NotSupportedException();

    public ExtPrivKey GetChannelKeyAtIndex(uint index) => (ExtPrivKey)_channelRoot.Derive((int)index, true).ToBytes();

    public ExtPrivKey GetDepositP2TrKeyAtIndex(uint index, bool isChange) => throw new NotSupportedException();
    public ExtPrivKey GetDepositP2WpkhKeyAtIndex(uint index, bool isChange) => throw new NotSupportedException();

    public CryptoKeyPair GetNodeKeyPair() => new(new PrivKey(_nodeKey.ToBytes()), NodeId);

    public CompactPubKey GetNodePubKey() => NodeId;

    public void ComputeNodeSharedSecret(ReadOnlySpan<byte> publicKey, Span<byte> sharedSecret)
    {
        var sharedPoint = new PubKey(publicKey.ToArray()).GetSharedPubkey(_nodeKey);
        SHA256.HashData(sharedPoint.ToBytes(), sharedSecret);
    }
}