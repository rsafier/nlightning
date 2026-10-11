namespace NLightning.Application.Tests.Gossip.Graph;

using Application.Gossip.Graph;
using Domain.Channels.ValueObjects;
using Domain.Gossip.Graph;

public sealed class GraphStoreSubscriptionTests
{
    [Fact]
    public async Task Given_ALiveGraph_When_PoliciesChange_Then_OnlyAcceptedExposedSnapshotsArePublished()
    {
        var kit = await GraphStoreTests.CreateGraphAsync();
        var changes = new List<GraphChange>();
        kit.Store.GraphChanged += (_, change) => changes.Add(change);
        var scid = new ShortChannelId(110, 1, 0);
        Assert.True(kit.Store.TryGetChannel(scid, out var channel));
        var policy = channel.Policy1! with { Timestamp = channel.Policy1!.Timestamp + 1, FeeBaseMsat = 1234 };

        Assert.True(kit.Store.TryApplyPolicy(scid, policy));
        Assert.False(kit.Store.TryApplyPolicy(scid, policy));
        var v2 = policy with { GossipVersion = 2, Timestamp = 200, FeeBaseMsat = 5678 };
        Assert.True(kit.Store.TryApplyPolicy(scid, v2));
        Assert.True(kit.Store.TryApplyPolicy(scid, policy with { Timestamp = policy.Timestamp + 1 }));

        Assert.Equal(2, changes.Count);
        Assert.Same(policy, changes[0].Policy);
        Assert.Equal(1234U, changes[0].Channel!.Policy1!.FeeBaseMsat);
        Assert.Same(v2, changes[1].Channel!.GetRoutingPolicy(0));
        Assert.Equal(GraphTestKit.TxIdFor(scid), changes[0].FundingTxId);
        Assert.NotSame(changes[0].Channel, changes[1].Channel);
    }

    [Fact]
    public async Task Given_ASpentGraphEdge_When_ReorgedAndPruned_Then_CloseRestoreAndCloseArePublishedOnce()
    {
        var kit = await GraphStoreTests.CreateGraphAsync();
        var changes = new List<GraphChange>();
        kit.Store.GraphChanged += (_, change) => changes.Add(change);
        var scid = new ShortChannelId(110, 1, 0);

        Assert.True(kit.Store.MarkSpent(scid, 300));
        Assert.True(kit.Store.MarkSpent(scid, 300));
        Assert.Equal(1, kit.Store.ClearSpentAbove(299));
        Assert.True(kit.Store.MarkSpent(scid, 301));
        Assert.True(kit.Store.RemoveChannel(scid));

        Assert.Equal(3, changes.Count);
        Assert.True(changes[0].Removed);
        Assert.Equal(300U, changes[0].ClosedHeight);
        Assert.False(changes[1].Removed);
        Assert.Null(changes[1].Channel!.SpentAtHeight);
        Assert.Equal(301U, changes[2].ClosedHeight);
        Assert.All(changes, c => Assert.Equal(GraphTestKit.TxIdFor(scid), c.FundingTxId));
    }

    [Fact]
    public async Task Given_ASubscriber_When_StartupLoadsAndNewNodeUpdatesArrive_Then_OnlyNewSnapshotsArePublished()
    {
        var original = await GraphStoreTests.CreateGraphAsync();
        await original.Store.FlushAsync(TestContext.Current.CancellationToken);
        var restarted = new GraphTestKit(original.Repository);
        var changes = new List<GraphChange>();
        restarted.Store.GraphChanged += (_, change) => changes.Add(change);
        await restarted.Store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Empty(changes);
        var node = restarted.Store.GetSnapshot().Nodes.First();
        var updated = new GraphNode(node.NodeId, node.Timestamp + 1, node.Features, node.Alias.Span,
                                    node.RgbColor.Span, node.Addresses);

        Assert.True(restarted.Store.TryApplyNode(updated));
        Assert.False(restarted.Store.TryApplyNode(updated));

        Assert.Same(updated, Assert.Single(changes).Node);
    }

    [Fact]
    public async Task Given_AFaultyObserver_When_AStaleEdgeIsRemoved_Then_TheGraphChangesAndOtherObserversSeeItsPoint()
    {
        var kit = await GraphStoreTests.CreateGraphAsync();
        kit.Store.GraphChanged += (_, _) => throw new InvalidOperationException("broken observer");
        GraphChange? observed = null;
        kit.Store.GraphChanged += (_, change) => observed = change;
        var scid = new ShortChannelId(110, 1, 0);

        Assert.True(kit.Store.RemoveChannel(scid));

        Assert.False(kit.Store.TryGetChannel(scid, out _));
        Assert.NotNull(observed);
        Assert.True(observed.Removed);
        Assert.Equal(0U, observed.ClosedHeight);
        Assert.Equal(GraphTestKit.TxIdFor(scid), observed.FundingTxId);
    }
}