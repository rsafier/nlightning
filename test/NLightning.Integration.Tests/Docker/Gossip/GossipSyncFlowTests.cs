using System.Diagnostics;

namespace NLightning.Integration.Tests.Docker.Gossip;

using Fixtures;
using Utils;

/// <summary>
/// BOLT 7 plan Proof G3 (gossip sync and relay) against LND 0.20 and a second NLightning node: (a) connected only to
/// bob, who is not a channel peer, our node syncs the fixture's graph by queries (<c>query_channel_range</c> sent,
/// <c>reply_channel_range</c> received) within 60 s; (b) while we are disconnected carol changes her fee on bob-carol,
/// and after we reconnect our graph has the new policy within two relay flushes; (c) our node relays alice's graph to
/// an NLightning node N2 that is connected only to us, and N2 never sends back a message it got from us (N2's traffic
/// recorded on the wire in both directions).
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

    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(2);

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
        Assert.True(traffic.CountSent(GossipTrafficRecorder.GossipTimestampFilterType) >= 1,
                    "we never sent gossip_timestamp_filter");
    }

    /// <summary>Proof G3 (b).</summary>
    [Fact]
    public async Task Given_SyncedFromBob_When_CarolChangesHerFeeWhileWeAreAway_Then_OurGraphHasItAfterReconnecting()
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

        var changes = new LndPolicyChanges();
        try
        {
            // Act 1: we leave; carol changes her fee on bob-carol and bob learns it
            node.PeerManager.DisconnectPeer(bob.LocalNodePubKeyBytes);
            await Poll.UntilAsync(() => !node.IsConnectedTo(bob.LocalNodePubKeyBytes), s_timeout,
                                  "disconnected from bob", ct, GossipGraphProbe.PollInterval);
            var original = await LndRoutingProbe.GetPolicyAsync(carol, bobCarol, carol.LocalNodePubKey, ct);
            var newBase = original.FeeBaseMsat + 1_000 + Random.Shared.Next(1, 1_000);
            var newPpm = (uint)original.FeeRateMilliMsat + 7;
            await changes.SetAsync(carol, bobCarol, newBase, newPpm, ct);
            await changes.WaitSeenByAsync(bob, ct);
            var stale = await GossipGraphProbe.TryGetOurGraphChannelAsync(node, bobCarol);
            var carolDirection = stale!.GetDirectionFrom(carol.LocalNodePubKeyBytes);
            Assert.NotEqual((uint)newBase, stale.GetPolicy(carolDirection)!.FeeBaseMsat);

            // Act 2: back
            var clock = Stopwatch.StartNew();
            await node.ConnectToAsync(bob, ct);

            // Assert: carol's new policy in our graph within two flushes
            await Poll.UntilAsync(async () =>
            {
                var policy = (await GossipGraphProbe.TryGetOurGraphChannelAsync(node, bobCarol))
                           ?.GetPolicy(carolDirection);
                Console.WriteLine($"Our graph: carol on bob-carol base {policy?.FeeBaseMsat}, ppm "
                                + $"{policy?.FeeProportionalMillionths}, timestamp {policy?.Timestamp}");
                return policy is not null && policy.FeeBaseMsat == (uint)newBase
                    && policy.FeeProportionalMillionths == newPpm;
            }, s_twoFlushes, "carol's new policy in our graph", ct, GossipGraphProbe.PollInterval);
            Console.WriteLine($"Caught up {clock.Elapsed} after reconnecting");
        }
        finally
        {
            await changes.RestoreAsync();
        }
    }

    /// <summary>Proof G3 (c).</summary>
    [Fact]
    public async Task Given_N2ConnectedOnlyToUs_When_WeRelayAlicesGraph_Then_N2LearnsItAndEchoesNothingBack()
    {
        // Arrange: we follow alice; N2 (gossip only, no channel) knows only us
        var ct = TestContext.Current.CancellationToken;
        var lndNodes = PublicTopology.LndNodes(_fixture);
        var alice = lndNodes[0];
        var scids = (await PublicTopology.GetFixtureChannelsAsync(_fixture, ct)).Select(c => c.ShortChannelId)
                                                                                 .ToList();
        var n2Traffic = new GossipTrafficRecorder();
        await using var node = await GossipTestNodes.StartGossipNodeAsync(_fixture, "gossip-g3c", "nltg-g3c", ct);
        await using var n2 = await GossipTestNodes.StartGossipNodeAsync(_fixture, "gossip-g3c-n2", "nltg-g3c-n2", ct,
                                                                        n => n.ConfigureServices = n2Traffic.Install);
        await ChainSync.WaitAllAtTipAsync(_fixture, [node, n2], ct);
        await node.ConnectToAsync(alice, ct);
        await Poll.UntilAsync(() => GossipGraphProbe.OurGraphHasAsync(node, scids, lndNodes), s_timeout,
                              "our graph complete from alice", ct, GossipGraphProbe.PollInterval);

        // Act
        await n2.ConnectToAsync(node, ct);

        // Assert: N2 has alice's graph, received from us (its only peer)
        await Poll.UntilAsync(() => GossipGraphProbe.OurGraphHasAsync(n2, scids, lndNodes), s_relayTimeout,
                              "N2's graph complete through our relay", ct, GossipGraphProbe.PollInterval);
        Assert.False(n2.IsConnectedTo(alice.LocalNodePubKeyBytes));
        var received = GossipWire.Summarize(n2Traffic.Received.Select(t => t.Wire));
        Assert.Empty(GossipWire.Missing(received, scids,
                                        lndNodes.Select(n => n.LocalNodePubKey.ToLowerInvariant())));

        // Assert: after one more flush, nothing N2 received went back to us, and nothing N2 sent came back to it
        await Task.Delay(TimeSpan.FromSeconds(70), ct);
        Console.WriteLine($"N2 traffic: {n2Traffic.Describe()}");
        var inbound = n2Traffic.Received.Where(t => t.Type is >= 256 and <= 258).Select(t => t.Hex).ToHashSet();
        var outbound = n2Traffic.Sent.Where(t => t.Type is >= 256 and <= 258).Select(t => t.Hex).ToHashSet();
        var echoed = inbound.Intersect(outbound).ToList();
        foreach (var hex in echoed.Take(5))
            Console.WriteLine($"Echoed: {hex[..Math.Min(hex.Length, 80)]}…");
        Assert.Empty(echoed);
        Assert.True(n2.IsConnectedTo(node.NodeId));
    }
}