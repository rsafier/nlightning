using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Tests.Payments.Trampoline.Harness;

using Application.Gossip.Graph.Interfaces;
using Application.Payments.Send;
using Application.Payments.Send.Interfaces;
using Application.Payments.Switch;
using Application.Payments.Trampoline;
using Channels.Harness;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Gossip.Graph;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Models;
using Infrastructure.Crypto.Hashes;
using Payments.Routing;
using Payments.Switch;
using TestUtils;

/// <summary>
/// Four in-process nodes A → T → X → C for the trampoline proofs (NL-875 TR5): each is a <see cref="SwitchNode"/> (a
/// production <c>ChannelManager</c>, <c>ChannelOperationsService</c>, <c>CommitScheduler</c> and
/// <see cref="HtlcSwitch"/>, real signers, real Sphinx, trampoline and error onions, its own SQLite database with the
/// real migrations and repositories), so a node can be restarted on the same keys and database.
/// </summary>
/// <remarks>
/// <para>Channels: A–T (A funds), T–X (T funds), X–C (X funds), each 2,000,000 sat with 800,000 sat pushed to the
/// fundee, and optionally a second A–T channel (<see cref="TrampolineHarnessOptions.SecondAliceTrampolineChannel"/>)
/// and a second T–X and X–C channel (<see cref="TrampolineHarnessOptions.SecondLegChannels"/>).
/// T and X forward with distinct policies (<see cref="TrampolineRouting"/>, <see cref="XRouting"/>), so a fee mix-up
/// shows. T and C advertise <c>trampoline_routing</c> by default (<see cref="TrampolineHarnessOptions.Trampoline"/>).
/// </para>
/// <para>Messages go through per-direction FIFOs that <see cref="PumpAsync"/> delivers round-robin, as
/// <see cref="ThreeNodeHarness"/> does; <see cref="RestartAsync"/> + <see cref="ReconnectAsync"/> restart a node and
/// stand in for <c>channel_reestablish</c> (only at a quiescent point: nothing is retransmitted).
/// <see cref="PumpUntilAsync{T}"/> pumps while a payment service call runs. Every node shares the stepped
/// <see cref="Clock"/> (MPP timers fire only from <see cref="AdvanceAsync"/>).</para>
/// <para>Onion helpers (hand-built outer and trampoline onions, failure decryption) are in
/// <c>TrampolineHarness.Onions.cs</c>; row readers in <c>TrampolineHarness.Rows.cs</c>.</para>
/// </remarks>
[ExcludeFromCodeCoverage]
internal sealed partial class TrampolineHarness : ISwitchNodeNetwork, IAsyncDisposable
{
    public const ulong FundingSatoshis = 2_000_000;
    public const ulong PushSatoshis = 800_000;
    public const uint InitialFeeratePerKw = 2_500;
    public const uint BlockHeight = ThreeNodeHarness.BlockHeight;

    public static readonly ChannelId AliceTrampolineChannelId = new(Enumerable.Repeat((byte)0xA7, 32).ToArray());
    public static readonly ChannelId AliceTrampoline2ChannelId = new(Enumerable.Repeat((byte)0xA8, 32).ToArray());
    public static readonly ChannelId TrampolineXChannelId = new(Enumerable.Repeat((byte)0x7E, 32).ToArray());
    public static readonly ChannelId XCarolChannelId = new(Enumerable.Repeat((byte)0xEC, 32).ToArray());
    public static readonly ChannelId TrampolineX2ChannelId = new(Enumerable.Repeat((byte)0x7F, 32).ToArray());
    public static readonly ChannelId XCarol2ChannelId = new(Enumerable.Repeat((byte)0xED, 32).ToArray());

    public static readonly ShortChannelId AliceTrampolineScid = new(400, 1, 0);
    public static readonly ShortChannelId TrampolineXScid = new(400, 2, 1);
    public static readonly ShortChannelId XCarolScid = new(400, 3, 0);
    public static readonly ShortChannelId AliceTrampoline2Scid = new(400, 4, 0);
    public static readonly ShortChannelId TrampolineX2Scid = new(400, 5, 1);
    public static readonly ShortChannelId XCarol2Scid = new(400, 6, 0);

    /// <summary>T's forwarding policy as a plain hop (its trampoline relay policy is the relay engine's own).</summary>
    public static RoutingOptions TrampolineRouting => new()
    {
        FeeBaseMsat = 1_000,
        FeeProportionalMillionths = 100,
        CltvExpiryDelta = 40
    };

    /// <summary>X's forwarding policy.</summary>
    public static RoutingOptions XRouting => new()
    {
        FeeBaseMsat = 2_000,
        FeeProportionalMillionths = 500,
        CltvExpiryDelta = 34
    };

    private static readonly SemaphoreSlim s_templateLock = new(1, 1);
    private static string? s_templatePath;

    private readonly string _directory;
    private readonly List<HarnessChannel> _channels = [];
    private readonly ConcurrentDictionary<(string From, string To), ConcurrentQueue<IChannelMessage>> _links = new();

    /// <summary>The payer.</summary>
    public SwitchNode A { get; }

    /// <summary>The trampoline node.</summary>
    public SwitchNode T { get; }

    /// <summary>The plain hop between T and C.</summary>
    public SwitchNode X { get; }

    /// <summary>The recipient.</summary>
    public SwitchNode C { get; }

    public IReadOnlyList<SwitchNode> Nodes => [A, T, X, C];

    /// <summary>The harness's channels, in opening order.</summary>
    public IReadOnlyList<HarnessChannel> Channels => _channels;

    public TrampolineHarnessOptions Options { get; }

    /// <summary>The stepped clock every node runs on (unless <see cref="TrampolineHarnessOptions.SteppedClock"/> is
    /// off).</summary>
    public SteppedTimeProvider Clock { get; } = new();

    /// <summary>Every message delivered, in delivery order.</summary>
    public List<SentMessage> Sent { get; } = [];

    private TrampolineHarness(string directory, TrampolineHarnessOptions options)
    {
        _directory = directory;
        Options = options;
        A = new SwitchNode(this, "A", 0xA1, Path.Combine(directory, "a.db"), new RoutingOptions());
        T = new SwitchNode(this, "T", 0x7A, Path.Combine(directory, "t.db"), TrampolineRouting);
        X = new SwitchNode(this, "X", 0x8E, Path.Combine(directory, "x.db"), XRouting);
        C = new SwitchNode(this, "C", 0xC0, Path.Combine(directory, "c.db"), new RoutingOptions());
    }

    /// <summary>Builds the four nodes, starts them on fresh databases and opens the channels.</summary>
    public static async Task<TrampolineHarness> CreateAsync(TrampolineHarnessOptions? options = null)
    {
        options ??= new TrampolineHarnessOptions();
        var directory = Path.Combine(Path.GetTempPath(), $"nltg-trampoline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var harness = new TrampolineHarness(directory, options);
        foreach (var node in harness.Nodes)
        {
            if (harness.Is(node, options.Trampoline))
            {
                node.Options.Features.OptionTrampolineRouting = FeatureSupport.Optional;
                node.Options.Features.AllowExperimentalFeatures = true;
            }

            options.ConfigureNode?.Invoke(node);
            var current = node;
            node.ConfigureServices = services => harness.ConfigureNodeServices(current, services);
        }

        // Every node starts on a copy of one migrated database (the migrations run once per test process)
        var template = await GetMigratedTemplateAsync();
        foreach (var node in harness.Nodes)
        {
            File.Copy(template, node.DatabasePath);
            await node.StartAsync(migrate: false);
        }

        await harness.OpenChannelAsync(harness.A, 1, harness.T, 1, AliceTrampolineChannelId, AliceTrampolineScid,
                                       0x71);
        await harness.OpenChannelAsync(harness.T, 2, harness.X, 1, TrampolineXChannelId, TrampolineXScid, 0x72);
        await harness.OpenChannelAsync(harness.X, 2, harness.C, 1, XCarolChannelId, XCarolScid, 0x73);
        if (options.SecondAliceTrampolineChannel)
            await harness.OpenChannelAsync(harness.A, 2, harness.T, 3, AliceTrampoline2ChannelId,
                                           AliceTrampoline2Scid, 0x74);
        if (options.SecondLegChannels)
        {
            await harness.OpenChannelAsync(harness.T, 4, harness.X, 3, TrampolineX2ChannelId, TrampolineX2Scid, 0x75);
            await harness.OpenChannelAsync(harness.X, 4, harness.C, 2, XCarol2ChannelId, XCarol2Scid, 0x76);
        }

        return harness;
    }

    /// <summary>Whether <paramref name="node"/> is one of <paramref name="nodes"/>.</summary>
    public bool Is(SwitchNode node, TrampolineHarnessNodes nodes) => (nodes & FlagOf(node)) != 0;

    public TrampolineHarnessNodes FlagOf(SwitchNode node) =>
        node == A ? TrampolineHarnessNodes.A
        : node == T ? TrampolineHarnessNodes.T
        : node == X ? TrampolineHarnessNodes.X
        : node == C ? TrampolineHarnessNodes.C
        : throw new ArgumentException("Not a node of this harness.", nameof(node));

    public SwitchNode Find(CompactPubKey nodeId) => Nodes.Single(n => n.NodeId == nodeId);

    /// <summary>The first channel between <paramref name="a"/> and <paramref name="b"/> (in opening order).</summary>
    public HarnessChannel ChannelBetween(SwitchNode a, SwitchNode b) =>
        _channels.First(c => (c.Funder == a && c.Fundee == b) || (c.Funder == b && c.Fundee == a));

    /// <summary>
    /// Delivers queued messages, one per link in turn, until every queue is empty and no commit scheduler has a
    /// signature waiting.
    /// </summary>
    public async Task PumpAsync()
    {
        await WhenReplaysIdleAsync();
        for (var steps = 0; steps < 50_000; steps++)
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
    /// Pumps while <paramref name="operation"/> (e.g. a payment service call, which returns once its outcome is known)
    /// runs, then once more, and returns its result.
    /// </summary>
    public async Task<T> PumpUntilAsync<T>(Task<T> operation, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (!operation.IsCompleted)
        {
            await PumpAsync();
            if (operation.IsCompleted)
                break;
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException("The operation did not complete while the harness pumped");

            await Task.WhenAny(operation, Task.Delay(10));
        }

        await PumpAsync();
        return await operation;
    }

    /// <summary>
    /// Moves the shared clock by <paramref name="by"/> (firing the timers due, such as <c>mpp_timeout</c>), waits for
    /// every switch's background work, then pumps.
    /// </summary>
    public async Task AdvanceAsync(TimeSpan by)
    {
        Clock.Advance(by);
        foreach (var node in Nodes.Where(n => n.IsRunning))
            await SwitchOf(node).WhenIdleAsync();
        await PumpAsync();
    }

    /// <summary>
    /// Stops <paramref name="node"/> (queued messages to and from it are lost; its peers see it disconnected), then
    /// starts it again from its database and registers every stored channel (the startup replay runs while no link
    /// is up), then runs the daemon's startup steps that follow: the payment reconciliation
    /// (<c>IPaymentOutcomeHandler.ReconcileInFlightPaymentsAsync</c>) and the trampoline relays' resumption
    /// (<see cref="TrampolineRelayService.StartAsync"/>), when the node has them. Call <see cref="ReconnectAsync"/>
    /// next.
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

        // As NltgDaemonService: every channel is loaded, so settle the payments, then resume the relays
        if (node.Services.GetService<IPaymentOutcomeHandler>() is { } payments)
            await payments.ReconcileInFlightPaymentsAsync(TestContext.Current.CancellationToken);
        if (node.Services.GetService<TrampolineRelayService>() is { } relays)
            await relays.StartAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Stands in for <c>channel_reestablish</c> at a quiescent point: every link of <paramref name="node"/> comes up
    /// through the production (decorated) <c>IPeerLivenessProbe</c>, whose link-up replay hands the pending events to
    /// the switch, as in the daemon.
    /// </summary>
    public async Task ReconnectAsync(SwitchNode node)
    {
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

        foreach (var peer in Nodes.Where(n => n != node && HasChannel(node, n)))
            await ReconnectLinkAsync(node, peer);
    }

    /// <summary>The link between <paramref name="a"/> and <paramref name="b"/> drops without a restart.</summary>
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
    /// Stands in for <c>channel_reestablish</c> between <paramref name="a"/> and <paramref name="b"/>: both ends of
    /// every channel between them call the production <c>MarkLinkUp</c>; waits for the replays it triggers.
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
    /// The gossip graph a <see cref="TrampolineHarnessOptions.GraphViewers"/> node's payment service sees: every
    /// channel with both directions' policies (each side's node routing options, as its <c>channel_update</c> says) and
    /// every node's <c>node_announcement</c> features (bit 57 on the trampoline nodes). Built on every read, so a
    /// test that changes a node's options sees it at the next route.
    /// </summary>
    public GraphSnapshot BuildGraph()
    {
        var timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var graph = new SyntheticGraph { Timestamp = timestamp };
        foreach (var channel in _channels)
            graph.Channel(channel.Scid, channel.Funder.NodeId, channel.Fundee.NodeId, channel.FundingSatoshis,
                          PolicyOf(channel.Funder), PolicyOf(channel.Fundee));
        foreach (var node in Nodes)
        {
            var features = node.Options.Features.GetNodeFeatures(FeatureContext.NodeAnnouncement).GetWireBytes()
                        ?? [];
            var alias = new byte[GraphNode.AliasLength];
            System.Text.Encoding.ASCII.GetBytes(node.Name).CopyTo(alias, 0);
            graph.Node(new GraphNode(node.NodeId, timestamp, features, alias, new byte[GraphNode.ColorLength]));
        }

        return graph.Build();
    }

    public void Route(SwitchNode from, CompactPubKey to, IChannelMessage message)
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

    public async ValueTask DisposeAsync()
    {
        foreach (var node in Nodes)
            await node.StopAsync();

        foreach (var node in Nodes)
            SqliteTestPools.Clear(node.DatabasePath);
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // Best effort: a file still held by the OS is left in the temp folder
        }
    }

    /// <summary>
    /// An empty database with every migration applied, made once per test process by a throwaway node (deleted when
    /// the process exits); SQLite's default rollback journal leaves a closed database in that one file.
    /// </summary>
    private static async Task<string> GetMigratedTemplateAsync()
    {
        await s_templateLock.WaitAsync();
        try
        {
            if (s_templatePath is not null && File.Exists(s_templatePath))
                return s_templatePath;

            var directory = Path.Combine(Path.GetTempPath(), $"nltg-trampoline-template-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "template.db");
            var node = new SwitchNode(new NoNetwork(), "template", 0x11, path, new RoutingOptions());
            await node.StartAsync(migrate: true);
            await node.StopAsync();
            SqliteTestPools.Clear(path);
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try
                {
                    Directory.Delete(directory, true);
                }
                catch (IOException)
                {
                    // Best effort
                }
            };
            s_templatePath = path;
            return path;
        }
        finally
        {
            s_templateLock.Release();
        }
    }

    private void ConfigureNodeServices(SwitchNode node, IServiceCollection services)
    {
        // Before AddPaymentSendServices' TryAdd(TimeProvider.System)
        if (Options.SteppedClock)
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Clock));
        if (Options.LogLevel is { } logLevel)
        {
            services.AddSingleton<ILoggerProvider>(new HarnessLoggerProvider(node.Name, logLevel));
            services.Configure<LoggerFilterOptions>(o => o.MinLevel = logLevel);
        }

        if (Is(node, Options.PaymentSenders))
        {
            services.AddPaymentSendServices();
            if (Is(node, Options.GraphViewers))
            {
                var graphStore = new Mock<IGraphStore>();
                graphStore.Setup(s => s.GetSnapshot()).Returns(() => BuildGraph());
                services.AddSingleton(graphStore.Object);
            }
        }

        Options.ConfigureServices?.Invoke(node, services);
    }

    private static SyntheticGraph.Policy PolicyOf(SwitchNode node)
    {
        var routing = node.Options.Routing;
        return new SyntheticGraph.Policy(routing.FeeBaseMsat, routing.FeeProportionalMillionths,
                                         routing.CltvExpiryDelta, 1_000_000_000, routing.HtlcMinimumMsat);
    }

    private bool HasChannel(SwitchNode a, SwitchNode b) =>
        _channels.Any(c => (c.Funder == a && c.Fundee == b) || (c.Funder == b && c.Fundee == a));

    private async Task<bool> DeliverNextAsync((string From, string To) key)
    {
        if (!_links.TryGetValue(key, out var queue) || !queue.TryDequeue(out var message))
            return false;

        var from = Nodes.Single(n => n.Name == key.From);
        var to = Nodes.Single(n => n.Name == key.To);
        to.Received.Add(message);

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
        _channels.Add(new HarnessChannel(funder, fundee, channelId, scid, FundingSatoshis, PushSatoshis));
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
        // Our key first, as the channel layer builds it (ChannelModel.GetSigningInfo, NL-495)
        var fundingOutput = new FundingOutputInfo(LightningMoney.Satoshis(FundingSatoshis),
                                                  selfBasepoints.FundingPubKey, peerBasepoints.FundingPubKey,
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

    /// <summary>A node's production switch (the one behind its recording <c>IHtlcSwitch</c>).</summary>
    public static HtlcSwitch SwitchOf(SwitchNode node) => node.Services.GetRequiredService<HtlcSwitch>();

    /// <summary>The template node's network: it never publishes.</summary>
    private sealed class NoNetwork : ISwitchNodeNetwork
    {
        public void Route(SwitchNode from, CompactPubKey to, IChannelMessage message) =>
            throw new InvalidOperationException("The template node has no peers");
    }
}

/// <summary>One channel of a <see cref="TrampolineHarness"/>.</summary>
/// <param name="Funder">The node that funded it (and pushed <paramref name="PushSatoshis"/>).</param>
/// <param name="Fundee">The other end.</param>
[ExcludeFromCodeCoverage]
internal sealed record HarnessChannel(
    SwitchNode Funder,
    SwitchNode Fundee,
    ChannelId ChannelId,
    ShortChannelId Scid,
    ulong FundingSatoshis,
    ulong PushSatoshis);