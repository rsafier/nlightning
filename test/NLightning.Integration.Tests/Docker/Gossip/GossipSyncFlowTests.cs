using System.Diagnostics;
using LNUnit.LND;

namespace NLightning.Integration.Tests.Docker.Gossip;

using Domain.Gossip.Graph;
using Fixtures;
using Utils;

/// <summary>
/// BOLT 7 plan Proof G3 (gossip sync and relay) against LND 0.20 and a second NLightning node: (a) connected only to
/// bob, who is not a channel peer, our node syncs the fixture's graph by queries (<c>query_channel_range</c> sent,
/// <c>reply_channel_range</c> received, the channels announced before our first <c>gossip_timestamp_filter</c>) within
/// 60 s; (b) while our node is down carol changes her fee on bob-carol, the restarted node reloads the old graph before
/// any connection and after it reconnects has the new policy within two relay flushes; (c) alice's graph is relayed
/// along us → N2 → N3 (NLightning nodes) and no relayed message goes back to the peer it came from.
/// </summary>
/// <remarks>
/// <para>None of these tests sends <c>gossip_timestamp_filter</c> by hand (unlike the G0/G2 proofs): the sync is our
/// node's own (G3-T2).</para>
/// <para>Run with <c>scripts/run-gossip.sh</c> (own process, own fixture). Needs lane C1 (G3-T1..T4); written against
/// the plan in parallel with it.</para>
/// </remarks>
[Collection(GossipRegtestCollection.Name)]
public class GossipSyncFlowTests
{
    /// <summary>(a): the plan's bound for the initial sync from one peer.</summary>
    private static readonly TimeSpan s_initialSyncTimeout = TimeSpan.FromSeconds(60);

    /// <summary>(b), (c): two relay flushes (<c>Gossip:OwnGossipFlushInterval</c>/G3-T3, 60 s each) and a margin.</summary>
    private static readonly TimeSpan s_twoFlushes = TimeSpan.FromSeconds(150);

    /// <summary>(c): N2 learns alice's graph through our relay (backlog after its filter, then flushes).</summary>
    private static readonly TimeSpan s_relayTimeout = TimeSpan.FromMinutes(3);

    /// <summary>(c): one relay flush (60 s) and a margin, over which no echo may appear.</summary>
    private static readonly TimeSpan s_oneFlush = TimeSpan.FromSeconds(70);

    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(2);

    /// <summary>(b): how long the restarted node gets to reconnect to its stored peer by itself.</summary>
    private static readonly TimeSpan s_ownReconnectWait = TimeSpan.FromSeconds(15);

    private readonly LightningRegtestNetworkFixture _fixture;

    public GossipSyncFlowTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    /// <summary>Proof G3 (a).</summary>
    [Fact]
    public async Task Given_OnlyBobConnected_When_WeSyncByQueries_Then_OurGraphIsCompleteWithinOneMinute()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var lndNodes = PublicTopology.LndNodes(_fixture);
        var bob = lndNodes[1];
        Console.WriteLine($"LND version: {await LndTestHelpers.GetVersionAsync(bob, ct)}");
        var scids = (await PublicTopology.GetFixtureChannelsAsync(_fixture, ct)).Select(c => c.ShortChannelId)
                                                                                 .ToList();
        var traffic = new GossipTrafficRecorder();
        await using var node = await GossipTestNodes.StartGossipNodeAsync(_fixture, "gossip-g3a", "nltg-g3a", ct,
                                                                          n => n.ConfigureServices = traffic.Install);
        await ChainSync.WaitAllAtTipAsync(_fixture, [node], ct);

        // Act
        var clock = Stopwatch.StartNew();
        await node.ConnectToAsync(bob, ct);

        // Assert: the whole fixture graph within a minute, through our queries
        await Poll.UntilAsync(() => GossipGraphProbe.OurGraphHasAsync(node, scids, lndNodes), s_initialSyncTimeout,
                              "our graph complete from bob's replies", ct, GossipGraphProbe.PollInterval);
        Console.WriteLine($"Graph complete {clock.Elapsed} after connecting to bob; traffic: {traffic.Describe()}");
        Assert.True(node.IsConnectedTo(bob.LocalNodePubKeyBytes));
        Assert.False(node.IsConnectedTo(lndNodes[0].LocalNodePubKeyBytes));
        Assert.True(traffic.CountSent(GossipTrafficRecorder.QueryChannelRangeType) >= 1,
                    "we never sent query_channel_range");
        Assert.True(traffic.CountReceived(GossipTrafficRecorder.ReplyChannelRangeType) >= 1,
                    "bob never answered with reply_channel_range");
        Assert.True(traffic.CountSent(GossipTrafficRecorder.QueryShortChannelIdsType) >= 1,
                    "we never sent query_short_channel_ids");
        Assert.True(traffic.CountReceived(GossipTrafficRecorder.ReplyShortChannelIdsEndType) >= 1,
                    "bob never ended a reply with reply_short_channel_ids_end");
        Assert.True(traffic.CountSent(GossipTrafficRecorder.GossipTimestampFilterType) >= 1,
                    "we never sent gossip_timestamp_filter");

        // Assert: the graph came from the queries, not from bob's dump after a timestamp filter: every fixture
        // channel was announced to us before our first gossip_timestamp_filter went out (plan §3.7: the range query,
        // then the SCID queries, then the filter; LND sends no gossip before a filter)
        var beforeFilter = traffic.Traffic
                                  .TakeWhile(t => !(t.Outbound
                                                 && t.Type == GossipTrafficRecorder.GossipTimestampFilterType))
                                  .Where(t => !t.Outbound)
                                  .Select(t => t.Wire);
        var queried = GossipWire.Summarize(beforeFilter);
        Console.WriteLine($"Received before our first filter: {queried}");
        Assert.All(scids, scid => Assert.Contains(scid, queried.AnnouncedChannels));
    }

    /// <summary>Proof G3 (b), with a restart: the graph survives it, and what changed while we were away is learnt
    /// after the reconnect (goal proof (d)).</summary>
    [Fact]
    public async Task Given_SyncedFromBob_When_CarolChangesHerFeeWhileWeAreDown_Then_OurGraphHasItAfterReconnecting()
    {
        // Arrange: synced from bob
        var ct = TestContext.Current.CancellationToken;
        var lndNodes = PublicTopology.LndNodes(_fixture);
        var (bob, carol) = (lndNodes[1], lndNodes[2]);
        var channels = await PublicTopology.GetFixtureChannelsAsync(_fixture, ct);
        var scids = channels.Select(c => c.ShortChannelId).ToList();
        var bobCarol = PublicTopology.FixtureChannelBetween(channels, bob, carol);
        await using var node = await GossipTestNodes.StartGossipNodeAsync(_fixture, "gossip-g3b", "nltg-g3b", ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [node], ct);
        await node.ConnectToAsync(bob, ct);
        await Poll.UntilAsync(() => GossipGraphProbe.OurGraphHasAsync(node, scids, lndNodes), s_timeout,
                              "our graph complete from bob", ct, GossipGraphProbe.PollInterval);
        var carolDirection = (await GossipGraphProbe.TryGetOurGraphChannelAsync(node, bobCarol))!
           .GetDirectionFrom(carol.LocalNodePubKeyBytes);
        var original = await LndRoutingProbe.GetPolicyAsync(carol, bobCarol, carol.LocalNodePubKey, ct);
        var newBase = original.FeeBaseMsat + 1_000 + Random.Shared.Next(1, 1_000);
        var newPpm = (uint)original.FeeRateMilliMsat + 7;

        var changes = new LndPolicyChanges();
        try
        {
            // Act 1: we stop (the graph is written on the way down); carol changes her fee on bob-carol and bob
            // learns it
            await node.StopAsync();
            await changes.SetAsync(carol, bobCarol, newBase, newPpm, ct);
            await changes.WaitSeenByAsync(bob, ct);

            // Act 2: we start again; the graph is read back before any peer connects (it still has carol's old
            // policy: nothing is reused from before the restart but the database), then bob is reconnected
            GraphChannel? reloaded = null;
            bool? connectedAtRead = null;
            node.BeforePeersStart = async n =>
            {
                connectedAtRead = n.IsConnectedTo(bob.LocalNodePubKeyBytes);
                reloaded = await GossipGraphProbe.TryGetOurGraphChannelAsync(n, bobCarol);
            };
            try
            {
                await node.StartAsync(ct);
            }
            finally
            {
                node.BeforePeersStart = null;
            }

            var clock = Stopwatch.StartNew();
            Assert.False(connectedAtRead);
            var stale = reloaded?.GetPolicy(carolDirection);
            Console.WriteLine($"Reloaded graph: carol on bob-carol base {stale?.FeeBaseMsat}, ppm "
                            + $"{stale?.FeeProportionalMillionths}, timestamp {stale?.Timestamp}");
            Assert.NotNull(stale);
            Assert.NotEqual((uint)newBase, stale.FeeBaseMsat);
            await ReconnectAsync(node, bob, ct);

            // Assert: carol's new policy in our graph within two flushes of the reconnect
            await Poll.UntilAsync(async () =>
            {
                var policy = (await GossipGraphProbe.TryGetOurGraphChannelAsync(node, bobCarol))
                           ?.GetPolicy(carolDirection);
                Console.WriteLine($"Our graph: carol on bob-carol base {policy?.FeeBaseMsat}, ppm "
                                + $"{policy?.FeeProportionalMillionths}, timestamp {policy?.Timestamp}");
                return policy is not null && policy.FeeBaseMsat == (uint)newBase
                    && policy.FeeProportionalMillionths == newPpm;
            }, s_twoFlushes, "carol's new policy in our graph", ct, GossipGraphProbe.PollInterval);
            Console.WriteLine($"Caught up {clock.Elapsed} after the restart");
            Assert.True(await GossipGraphProbe.OurGraphHasAsync(node, scids, lndNodes));
        }
        finally
        {
            await changes.RestoreAsync();
        }
    }

    /// <summary>Proof G3 (c).</summary>
    /// <remarks>
    /// A chain alice → us → N2 → N3 (N2 and N3 are gossip-only NLightning nodes without channels; each knows only its
    /// neighbours). N3 learning alice's graph proves that relayed gossip is relayed again (N2 got it from us). The echo
    /// check is on N2, the only node whose origin peer can be told apart on the wire: N2 has no gossip of its own, so
    /// every 256-258 it sends is a relay, and all of them must reach N3; one that did not went back to us, its origin
    /// (N2's and N3's traffic are recorded at the serializer, which does not know the peer). N3, whose only peer is its
    /// origin, must send none. Query answers count as sends too: the range queries each node runs when a peer connects
    /// find that peer's graph still empty (N2 before our relay, N3 before N2's), so they return no 256-258; a later
    /// query of ours to N2 (rotation after 20 min, the NL-353 re-query of dropped SCIDs) would show up as an echo.
    /// </remarks>
    [Fact]
    public async Task Given_N2BetweenUsAndN3_When_AlicesGraphIsRelayed_Then_N3LearnsItAndNothingGoesBackToItsOrigin()
    {
        // Arrange: we follow alice; N2 knows only us and N3, N3 knows only N2
        var ct = TestContext.Current.CancellationToken;
        var lndNodes = PublicTopology.LndNodes(_fixture);
        var alice = lndNodes[0];
        var scids = (await PublicTopology.GetFixtureChannelsAsync(_fixture, ct)).Select(c => c.ShortChannelId)
                                                                                 .ToList();
        var nodeIds = lndNodes.Select(n => n.LocalNodePubKey.ToLowerInvariant()).ToList();
        var n2Traffic = new GossipTrafficRecorder();
        var n3Traffic = new GossipTrafficRecorder();
        await using var node = await GossipTestNodes.StartGossipNodeAsync(_fixture, "gossip-g3c", "nltg-g3c", ct);
        await using var n2 = await GossipTestNodes.StartGossipNodeAsync(_fixture, "gossip-g3c-n2", "nltg-g3c-n2", ct,
                                                                        n => n.ConfigureServices = n2Traffic.Install);
        await using var n3 = await GossipTestNodes.StartGossipNodeAsync(_fixture, "gossip-g3c-n3", "nltg-g3c-n3", ct,
                                                                        n => n.ConfigureServices = n3Traffic.Install);
        await ChainSync.WaitAllAtTipAsync(_fixture, [node, n2, n3], ct);
        await node.ConnectToAsync(alice, ct);
        await Poll.UntilAsync(() => GossipGraphProbe.OurGraphHasAsync(node, scids, lndNodes), s_timeout,
                              "our graph complete from alice", ct, GossipGraphProbe.PollInterval);

        // Act
        await n2.ConnectToAsync(node, ct);
        await n3.ConnectToAsync(n2, ct);

        // Assert: N2 and then N3 have alice's graph, each from its only upstream peer
        await Poll.UntilAsync(() => GossipGraphProbe.OurGraphHasAsync(n2, scids, lndNodes), s_relayTimeout,
                              "N2's graph complete through our relay", ct, GossipGraphProbe.PollInterval);
        await Poll.UntilAsync(() => GossipGraphProbe.OurGraphHasAsync(n3, scids, lndNodes), s_relayTimeout,
                              "N3's graph complete through N2's relay", ct, GossipGraphProbe.PollInterval);
        Assert.False(n2.IsConnectedTo(alice.LocalNodePubKeyBytes));
        Assert.False(n3.IsConnectedTo(alice.LocalNodePubKeyBytes));
        Assert.False(n3.IsConnectedTo(node.NodeId));
        Assert.Empty(GossipWire.Missing(GossipWire.Summarize(n2Traffic.Received.Select(t => t.Wire)), scids, nodeIds));
        Assert.Empty(GossipWire.Missing(GossipWire.Summarize(n3Traffic.Received.Select(t => t.Wire)), scids, nodeIds));

        // Assert: through one more flush, every announcement or update N2 sent reached N3 (none went back to us), and
        // N3 sent none (none went back to N2); N3 is read after N2 so a message in flight is counted
        var noEcho = await Poll.HoldsAsync(() => EchoesToOrigin(n2Traffic, n3Traffic).Count == 0
                                              && GossipOf(n3Traffic.Sent).Count == 0,
                                           s_oneFlush, ct, TimeSpan.FromSeconds(5));
        Console.WriteLine($"N2 traffic: {n2Traffic.Describe()}; N3 traffic: {n3Traffic.Describe()}");
        foreach (var hex in EchoesToOrigin(n2Traffic, n3Traffic).Concat(GossipOf(n3Traffic.Sent)).Take(5))
            Console.WriteLine($"Echoed: {hex[..Math.Min(hex.Length, 80)]}…");
        Assert.True(noEcho, "a relayed message went back to the peer it came from");
        Assert.NotEmpty(GossipOf(n2Traffic.Sent));
        Assert.True(n2.IsConnectedTo(node.NodeId));
        Assert.True(n3.IsConnectedTo(n2.NodeId));
    }

    /// <summary>
    /// The 256-258 messages N2 sent more often than N3 received them (N2's only other peer is its origin, us).
    /// </summary>
    private static List<string> EchoesToOrigin(GossipTrafficRecorder n2, GossipTrafficRecorder n3)
    {
        var sent = GossipOf(n2.Sent);
        var received = GossipOf(n3.Received).GroupBy(h => h).ToDictionary(g => g.Key, g => g.Count());
        return sent.GroupBy(h => h)
                   .Where(g => g.Count() > received.GetValueOrDefault(g.Key))
                   .Select(g => g.Key)
                   .ToList();
    }

    private static List<string> GossipOf(IEnumerable<GossipTrafficRecorder.GossipTraffic> traffic) =>
        traffic.Where(t => t.Type is >= 256 and <= 258).Select(t => t.Hex).ToList();

    /// <summary>
    /// Waits for the restarted node's own reconnect to <paramref name="lnd"/> (<c>PeerManager</c> reconnects to its
    /// stored peers at start), connecting by hand if it does not come.
    /// </summary>
    private static async Task ReconnectAsync(NLightningTestNode node, LNDNodeConnection lnd, CancellationToken ct)
    {
        if (await Poll.HoldsAsync(() => !node.IsConnectedTo(lnd.LocalNodePubKeyBytes), s_ownReconnectWait, ct))
            await node.ConnectToAsync(lnd, ct);
        await Poll.UntilAsync(() => node.IsConnectedTo(lnd.LocalNodePubKeyBytes), s_timeout,
                              $"reconnected to {lnd.LocalAlias}", ct, GossipGraphProbe.PollInterval);
    }
}