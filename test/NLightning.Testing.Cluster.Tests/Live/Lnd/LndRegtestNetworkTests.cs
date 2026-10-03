using System.Diagnostics;

namespace NLightning.Testing.Cluster.Tests.Live.Lnd;

using Cluster.Nodes.Lnd;
using Cluster.Topology.Lnd;
using Testing.Lnd.Lnrpc;

/// <summary>
/// The Docker suites' LND regtest network on the cluster (test harness phase 3): <see cref="LndRegtestNetworkFixture"/>
/// builds bitcoind <c>miner</c> + alice, bob, carol and david on <c>custom_lnd:0.21.4-beta</c> with LNUnit's flags and
/// channels once for the class, in its own namespace. The tests check what the Docker fixture promises, that every
/// startup channel routes (alice → bob → carol included), and that an LND restart (graceful and a kill) keeps its
/// channels and its client. Each test leaves the network as it found it (channels active, nodes up), so the order does
/// not matter.
/// </summary>
/// <remarks>
/// Explicit, <c>Category=Cluster</c>: <c>scripts/run-cluster.sh -n 1 --class
/// NLightning.Testing.Cluster.Tests.Live.Lnd.LndRegtestNetworkTests</c> (<c>-n 2</c>: two networks at once).
/// </remarks>
[Trait("Category", "Cluster")]
public class LndRegtestNetworkTests(LndRegtestNetworkFixture fixture)
    : IClassFixture<LndRegtestNetworkFixture>, IAsyncLifetime
{
    private const long PaymentMsat = 10_000_000;

    private static readonly TimeSpan s_payTimeout = TimeSpan.FromMinutes(1);

    private LndRegtestNetwork Network => fixture.Network;

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    // The warm topology starts with the first test that runs, not when xunit creates the fixture (NL-800)
    public ValueTask InitializeAsync() => new(fixture.EnsureStartedAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact(Explicit = true)]
    public async Task Given_TheNetwork_When_Built_Then_ItIsTheDockerFixturesNetworkAndReady()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        foreach (var line in fixture.StartLog)
            Log(line);
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in Network.Nodes)
            ids[node.Alias] = await node.GetNodeIdAsync(ct);

        // Assert: four LND 0.21.4 nodes, SERVER_ACTIVE, each connected to every other one, through in-tree clients
        Assert.Equal(["alice", "bob", "carol", "david"], Network.Nodes.Select(n => n.Alias));
        foreach (var node in Network.Nodes)
        {
            var connection = Network.GetLndNode(node.Alias);
            Assert.Same(node.Connection, connection);
            Assert.Equal(ids[node.Alias], connection.LocalNodePubKey);
            Assert.Equal(node.Alias, connection.LocalAlias);
            Assert.True(connection.IsServerReady);
            var info = await connection.LightningClient.GetInfoAsync(new GetInfoRequest(), cancellationToken: ct);
            Assert.StartsWith("0.21.4-beta", info.Version, StringComparison.Ordinal);
            Assert.True(info.SyncedToChain);
            var peers = await connection.LightningClient.ListPeersAsync(new ListPeersRequest(), cancellationToken: ct);
            Assert.Equal(ids.Where(i => i.Key != node.Alias).Select(i => i.Value).Order(),
                         peers.Peers.Select(p => p.PubKey).Order());
        }

        // Assert: alice signals LND's simple close (--protocol.rbf-coop-close), bob does not
        var aliceFeatures = (await Network.GetLndNode("alice").LightningClient
                                          .GetInfoAsync(new GetInfoRequest(), cancellationToken: ct)).Features;
        var bobFeatures = (await Network.GetLndNode("bob").LightningClient
                                        .GetInfoAsync(new GetInfoRequest(), cancellationToken: ct)).Features;
        Assert.Contains(aliceFeatures.Keys, bit => bit is 60 or 61);
        Assert.DoesNotContain(bobFeatures.Keys, bit => bit is 60 or 61);

        // Assert: the four startup channels, public, active on both ends, in every graph with the funder's policy
        Assert.Equal(["alice->bob", "bob->alice", "carol->alice", "carol->bob"],
                     Network.Channels.Select(c => $"{c.Spec.From}->{c.Spec.To}"));
        foreach (var channel in Network.Channels)
        {
            var funder = Network.Node(channel.Spec.From);
            var listed = (await funder.Connection.LightningClient.ListChannelsAsync(new ListChannelsRequest(),
                                                                                     cancellationToken: ct))
                        .Channels.Single(c => c.ChanId == channel.ChanId);
            Assert.True(listed.Active);
            Assert.False(listed.Private);
            Assert.Equal(LndRegtestNetworkSpec.DefaultCapacitySat, listed.Capacity);
            Assert.Equal(ids[channel.Spec.To], listed.RemotePubkey);
            var peerSide = (await Network.GetLndNode(channel.Spec.To).LightningClient
                                         .ListChannelsAsync(new ListChannelsRequest(), cancellationToken: ct))
                          .Channels.Single(c => c.ChanId == channel.ChanId);
            Assert.True(peerSide.Active);
            Assert.Equal(channel.Spec.PushSat > 0, peerSide.LocalBalance > 0);
            foreach (var node in Network.Nodes)
            {
                var edge = await LndGraph.GetEdgeAsync(node, channel.ChanId, ct);
                Assert.Null(LndGraph.EdgeProblem(edge, ids[channel.Spec.From], 0, 0, 40));
            }
        }

        // Assert: the miner can still fund the tests (its reserve matured): more than 500 BTC left
        var minerSat = await Network.Chain.Chain.Rpc.GetTrustedBalanceSatAsync(ct);
        Assert.True(minerSat > 50_000_000_000, $"the miner has {minerSat} sat");
        var tip = await Network.Chain.GetBlockCountAsync(ct);
        Log($"{Network.Run.Namespace}: network ready in {fixture.StartTime.TotalSeconds:F1} s at height {tip}, miner "
          + $"{minerSat / 100_000_000m} BTC ("
          + string.Join(", ", Network.Timings.OrderBy(t => t.Value).Select(t => $"{t.Key} {t.Value.TotalSeconds:F1}"))
          + ")");
    }

    [Fact(Explicit = true)]
    public async Task Given_TheNetwork_When_EachChannelAndAliceBobCarolArePaid_Then_EveryChannelRoutes()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();
        var carolBob = Network.Channels.Single(c => c.Spec is { From: "carol", To: "bob" });

        // Act: each funder pays its peer over exactly that channel
        var single = new List<(LndRegtestChannel Channel, LndRoutedPayment Payment)>();
        foreach (var channel in Network.Channels)
            single.Add((channel, await Network.PayAlongAsync(channel.Spec.From, [channel.Spec.To], PaymentMsat,
                                                             channel.ChanId, s_payTimeout, ct)));
        // Act: alice -> bob -> carol (bob forwards over carol's channel, where he holds the push)
        var twoHops = await Network.PayAlongAsync("alice", ["bob", "carol"], PaymentMsat, null, s_payTimeout, ct);

        // Assert
        foreach (var (channel, payment) in single)
        {
            Assert.True(payment.Succeeded, $"{channel}: {payment.FailureReason}");
            Assert.Equal([channel.ChanId], payment.RouteChanIds);
        }

        Assert.True(twoHops.Succeeded, twoHops.FailureReason);
        Assert.Equal(2, twoHops.RouteChanIds.Count);
        Assert.Contains(twoHops.RouteChanIds[0],
                        Network.Channels.Where(c => c.Spec.From is "alice" or "bob" && c.Spec.To is "alice" or "bob")
                               .Select(c => c.ChanId));
        Assert.Equal(carolBob.ChanId, twoHops.RouteChanIds[1]);
        Log($"{Network.Run.Namespace}: {single.Count} channels paid in "
          + $"{string.Join("/", single.Select(s => s.Payment.Attempts))} attempt(s), alice > bob > carol in "
          + $"{twoHops.Attempts}, {watch.Elapsed.TotalSeconds:F1} s");
    }

    [Fact(Explicit = true)]
    public async Task Given_TheNetwork_When_AliceRestartsAndBobIsKilled_Then_TheyKeepTheirChannelsAndClients()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var alice = Network.Node("alice");
        var bob = Network.Node("bob");
        var aliceConnection = Network.GetLndNode("alice");
        var aliceId = await alice.GetNodeIdAsync(ct);
        var bobId = await bob.GetNodeIdAsync(ct);
        var aliceChannels = Network.Channels.Where(c => c.Spec.From == "alice" || c.Spec.To == "alice")
                                   .Select(c => c.ChanId).Order().ToList();

        // Act
        var restarted = await Network.RestartAsync("alice", kill: false, ct);
        var killed = await Network.RestartAsync("bob", kill: true, ct);
        var afterRestart = await Network.PayAlongAsync("alice", ["bob", "carol"], PaymentMsat, null, s_payTimeout,
                                                       ct);

        // Assert: new pods, the same nodes, clients and channels, all active again; and they route
        Assert.NotEqual(restarted.PodUidBefore, restarted.PodUidAfter);
        Assert.NotEqual(killed.PodUidBefore, killed.PodUidAfter);
        Assert.Same(aliceConnection, Network.GetLndNode("alice"));
        Assert.Equal(aliceId, (await aliceConnection.RefreshNodeInfoAsync(ct)).IdentityPubkey);
        Assert.Equal(bobId, (await Network.GetLndNode("bob").RefreshNodeInfoAsync(ct)).IdentityPubkey);
        Assert.True(restarted.ChannelsAwaited >= aliceChannels.Count);
        var listed = await aliceConnection.LightningClient.ListChannelsAsync(new ListChannelsRequest(),
                                                                             cancellationToken: ct);
        Assert.Equal(aliceChannels, listed.Channels.Where(c => aliceChannels.Contains(c.ChanId))
                                          .Select(c => c.ChanId).Order());
        Assert.All(listed.Channels.Where(c => aliceChannels.Contains(c.ChanId)), c => Assert.True(c.Active));
        await Network.WaitReadyAsync(ct);
        // Assert: the mesh is whole again, david included (no channel with bob: only the redial brings him back)
        await Network.WaitMeshAsync(ct);
        Assert.Equal(Network.Nodes.Count - 1, restarted.PeersRedialled);
        Assert.Equal(Network.Nodes.Count - 1, killed.PeersRedialled);
        Assert.True(afterRestart.Succeeded, afterRestart.FailureReason);
        Log($"{Network.Run.Namespace}: {restarted}; {killed}; alice > bob > carol after both in "
          + $"{afterRestart.Attempts} attempt(s)");
    }
}