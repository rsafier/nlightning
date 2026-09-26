using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Gossip.Metrics;

using Application.Gossip.Graph;
using Application.Gossip.Metrics;
using Application.Gossip.Relay;
using Application.Gossip.Sync;
using Domain.Channels.ValueObjects;
using Domain.Gossip.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.ValueObjects;
using Sync;

/// <summary>BOLT 7 plan G5-T4 (metrics half): the <c>NLightning.Gossip</c> meter and its registration.</summary>
public class GossipMetricsTests
{
    [Fact]
    public void Given_TheMeter_When_EveryCounterIsRecorded_Then_AListenerSeesThemWithTheirTags()
    {
        // Arrange
        using var metrics = new GossipMetrics();
        using var recorder = new GossipMetricsRecorder(metrics);

        // Act
        metrics.RecordReceived(MessageTypes.ChannelUpdate);
        metrics.RecordAccepted(MessageTypes.NodeAnnouncement);
        metrics.RecordRejected(MessageTypes.ChannelAnnouncement, GossipMetricReasons.GraphFull);
        metrics.RecordOrphaned(MessageTypes.ChannelUpdate);
        metrics.RecordDropped(GossipMetricReasons.QueueFull, 3);
        metrics.RecordDropped(GossipMetricReasons.QueueFull, 0);
        metrics.RecordRelayed(MessageTypes.ChannelUpdate, "own");
        metrics.RecordChainLookup("Found");
        metrics.RecordPeerBanned();
        metrics.RecordSyncDuration(TimeSpan.FromSeconds(2.5), completed: false);

        // Assert
        Assert.Equal(GossipMetrics.MeterName, metrics.Meter.Name);
        Assert.Equal(1, recorder.Sum("nlightning.gossip.messages.received", (GossipMetrics.TypeTag, "channel_update")));
        Assert.Equal(1, recorder.Sum("nlightning.gossip.messages.accepted",
                                     (GossipMetrics.TypeTag, "node_announcement")));
        Assert.Equal(1, recorder.Sum("nlightning.gossip.messages.rejected",
                                     (GossipMetrics.TypeTag, "channel_announcement"),
                                     (GossipMetrics.ReasonTag, GossipMetricReasons.GraphFull)));
        Assert.Equal(1, recorder.Sum("nlightning.gossip.messages.orphaned"));
        Assert.Equal(3, recorder.Sum("nlightning.gossip.messages.dropped",
                                     (GossipMetrics.ReasonTag, GossipMetricReasons.QueueFull)));
        Assert.Equal(1, recorder.Count("nlightning.gossip.messages.dropped"));
        Assert.Equal(1, recorder.Sum("nlightning.gossip.messages.relayed", (GossipMetrics.PathTag, "own")));
        Assert.Equal(1, recorder.Sum("nlightning.gossip.chain.lookups", (GossipMetrics.StatusTag, "Found")));
        Assert.Equal(1, recorder.Sum("nlightning.gossip.peers.banned"));
        Assert.Equal(2.5, recorder.Sum("nlightning.gossip.sync.duration", (GossipMetrics.OutcomeTag, "failed")));
    }

    [Fact]
    public void Given_QueueSources_When_Observed_Then_EachIsReportedAndAFailingOneIsSkipped()
    {
        // Arrange
        using var metrics = new GossipMetrics();
        using var recorder = new GossipMetricsRecorder(metrics);
        var depth = 7L;
        metrics.RegisterQueue("a", () => depth);
        metrics.RegisterQueue("broken", () => throw new InvalidOperationException("gone"));

        // Act
        var first = recorder.ObserveQueue("a");
        depth = 9;
        metrics.RegisterQueue("a", () => depth * 2);
        var replaced = recorder.ObserveQueue("a");

        // Assert
        Assert.Equal(7, first);
        Assert.Equal(18, replaced);
        Assert.True(double.IsNaN(recorder.ObserveQueue("broken")));
    }

    [Fact]
    public async Task Given_ARangeSync_When_ItCompletes_Then_ItsDurationIsRecorded()
    {
        // Arrange (G5-T4: sync durations)
        using var metrics = new GossipMetrics();
        using var recorder = new GossipMetricsRecorder(metrics);
        var graph = new SyncTestGraph();
        var known = graph.AddSignedChannel(new ShortChannelId(100, 0, 0), SyncTestGraph.NodeA, SyncTestGraph.NodeB);
        var ingress = new Mock<IGossipIngress>();
        ingress.SetupGet(i => i.IsEnabled).Returns(true);
        using var manager = new GossipSyncManager(graph.Store,
                                                  Microsoft.Extensions.Options.Options.Create(new GossipSyncOptions()),
                                                  Microsoft.Extensions.Options.Options.Create(new NodeOptions
                                                  {
                                                      BitcoinNetwork = BitcoinNetwork.Regtest
                                                  }), NullLogger<GossipSyncManager>.Instance, graph.Kit.Clock,
                                                  ingress.Object, getTipHeight: () => 500, metrics: metrics);
        var peer = new FakeGossipPeer(1);
        manager.OnPeerInitialized(peer);
        await peer.NextAsync<QueryChannelRangeMessage>();

        // Act
        manager.HandleMessage(peer, RangeReplyCollectorTests.Reply(0, 501, true, known.ShortChannelId));
        await peer.NextAsync<GossipTimestampFilterMessage>();
        await manager.WhenIdleAsync(peer, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, recorder.Count("nlightning.gossip.sync.duration"));
        Assert.True(recorder.Sum("nlightning.gossip.sync.duration", (GossipMetrics.OutcomeTag, "completed")) >= 0);
    }

    [Fact]
    public void Given_TheGraphAndRelayRegistrations_When_Built_Then_OneMeterIsSharedByIngressAndRelay()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddGossipGraphServices();
        services.AddGossipRelayServices();
        services.AddGossipMetrics();

        // Assert
        Assert.Single(services, d => d.ServiceType == typeof(GossipMetrics));
        using var provider = services.BuildServiceProvider();
        Assert.Same(provider.GetRequiredService<GossipMetrics>(), provider.GetRequiredService<GossipMetrics>());
    }

    [Fact]
    public void Given_InvalidLimits_When_Validated_Then_EachIsReported()
    {
        // Arrange
        var graph = new GossipGraphOptions
        {
            MaxChannels = 0,
            MaxNodes = 0,
            ChannelUpdateBurst = 0,
            MaxFutureTimestamp = TimeSpan.Zero,
            MisbehaviourThreshold = 1,
            MisbehaviourWindow = TimeSpan.Zero,
            MisbehaviourBanDuration = TimeSpan.Zero
        };
        var relay = new GossipRelayOptions { MaxRelayPendingPerPeer = 0 };

        // Act
        var graphErrors = graph.GetValidationErrors();
        var relayErrors = relay.GetValidationErrors();

        // Assert
        Assert.Equal(6, graphErrors.Count);
        Assert.Single(relayErrors);
        Assert.Empty(new GossipGraphOptions().GetValidationErrors());
        Assert.Equal(TimeSpan.FromDays(14), new GossipGraphOptions().MaxFutureTimestamp);
    }
}