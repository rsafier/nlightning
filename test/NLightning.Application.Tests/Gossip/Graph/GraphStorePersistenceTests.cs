namespace NLightning.Application.Tests.Gossip.Graph;

using Application.Gossip.Graph;
using Application.Gossip.Metrics;
using Domain.Channels.ValueObjects;
using Domain.Gossip.Graph;
using Metrics;

/// <summary>
/// BOLT 7 plan G5-T3 in the store: the batched write-behind flush, the batched startup load and the memory estimate
/// (G5-T1) kept up to date by every change. The SQLite equivalents are in
/// <c>Integration.Tests/Persistence/GraphStoreLoadPerformanceTests</c> and <c>GraphDbRepositoryBulkTests</c>.
/// </summary>
public class GraphStorePersistenceTests
{
    private static readonly TestGossipKey s_alice = new(1);
    private static readonly TestGossipKey s_bob = new(2);
    private static readonly uint s_now = (uint)GraphTestKit.DefaultNow.ToUnixTimeSeconds();
    private static readonly ShortChannelId s_ab = new(110, 1, 0);
    private static readonly ShortChannelId s_bc = new(115, 1, 0);

    [Fact]
    public async Task Given_MoreChangesThanABatch_When_Flushed_Then_TheyAreWrittenInSeveralSavesInWriteOrder()
    {
        // Arrange: 2 channels, 3 policies and 2 nodes = 7 rows, batches of 3
        var kit = await GraphStoreTests.CreateGraphAsync(new GraphTestKit(writeBatchSize: 3));

        // Act
        await kit.Store.FlushAsync(TestContext.Current.CancellationToken);

        // Assert: ceil(7 / 3) saves, and every row written (a policy's channel went first, else the FK check throws)
        Assert.Equal(3, kit.Repository.Saves);
        Assert.Equal(0, kit.Store.PendingChanges);
        Assert.Equal(2, kit.Repository.Channels.Count);
        Assert.Equal(3, kit.Repository.Policies.Count);
        Assert.Equal(2, kit.Repository.Nodes.Count);
    }

    [Fact]
    public async Task Given_GossipMetrics_When_TheStoreFlushesAndLoads_Then_ItRecordsBothAndItsWriteBehindDepth()
    {
        // Arrange (G-D seam: the D2 store paths record into the D1 meter)
        using var metrics = new GossipMetrics();
        using var recorder = new GossipMetricsRecorder(metrics);
        var kit = await GraphStoreTests.CreateGraphAsync(new GraphTestKit(metrics: metrics));
        var pendingBeforeFlush = recorder.ObserveQueue("graph_write_behind");

        // Act
        await kit.Store.FlushAsync(TestContext.Current.CancellationToken);
        var pendingAfterFlush = recorder.ObserveQueue("graph_write_behind");
        var restarted = new GraphTestKit(kit.Repository, metrics: metrics);
        await restarted.Store.LoadAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(7, pendingBeforeFlush);
        Assert.Equal(0, pendingAfterFlush);
        Assert.Equal(1, recorder.Count("nlightning.gossip.store.duration", (GossipMetrics.OperationTag, "flush"),
                                       (GossipMetrics.OutcomeTag, "completed")));
        Assert.Equal(1, recorder.Count("nlightning.gossip.store.duration", (GossipMetrics.OperationTag, "load"),
                                       (GossipMetrics.OutcomeTag, "completed")));
    }

    [Fact]
    public async Task Given_FewChanges_When_Flushed_Then_TheyAreOneSave()
    {
        // Arrange
        var kit = await GraphStoreTests.CreateGraphAsync();

        // Act
        await kit.Store.FlushAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, kit.Repository.Saves);
        Assert.Equal(0, kit.Store.PendingChanges);
    }

    [Fact]
    public async Task Given_TheSecondBatchFails_When_Flushed_Then_TheFirstIsWrittenAndOnlyTheRestStaysPending()
    {
        // Arrange: batch 1 = both channels and one policy, batch 2 fails, batch 3 is never tried
        var kit = await GraphStoreTests.CreateGraphAsync(new GraphTestKit(writeBatchSize: 3));
        kit.Repository.FailAtAttempt = 2;

        // Act
        await kit.Store.FlushAsync(TestContext.Current.CancellationToken);
        var pendingAfterFailure = kit.Store.PendingChanges;
        var channelsAfterFailure = kit.Repository.Channels.Count;
        var policiesAfterFailure = kit.Repository.Policies.Count;
        await kit.Store.FlushAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(4, pendingAfterFailure);
        Assert.Equal(2, channelsAfterFailure);
        Assert.Equal(1, policiesAfterFailure);
        Assert.Equal(0, kit.Store.PendingChanges);
        Assert.Equal(3, kit.Repository.Policies.Count);
        Assert.Equal(2, kit.Repository.Nodes.Count);
    }

    [Fact]
    public async Task Given_AGraph_When_LoadedOneRowPerBatch_Then_ItEqualsTheFlushedGraphAndItsEstimate()
    {
        // Arrange
        var kit = await GraphStoreTests.CreateGraphAsync();
        await kit.Store.FlushAsync(TestContext.Current.CancellationToken);
        var restarted = new GraphTestKit(kit.Repository, loadBatchSize: 1);

        // Act
        await restarted.Store.LoadAsync(TestContext.Current.CancellationToken);

        // Assert
        GraphTestKit.AssertSameGraph(kit.Store.GetSnapshot(), restarted.Store.GetSnapshot());
        Assert.Equal(kit.Store.GetMemoryEstimate(), restarted.Store.GetMemoryEstimate());
        Assert.Equal(3, restarted.Store.PolicyCount);
    }

    [Fact]
    public async Task Given_ChangesBetweenTheLoadsBatches_When_Loaded_Then_TheInMemoryChangesWin()
    {
        // Arrange: a flushed graph (channels ab then bc, policies of ab then bc, nodes alice then carol) loaded one row
        // per batch; the store is changed while the load waits for the next row, with the writer lock released
        var kit = await GraphStoreTests.CreateGraphAsync();
        await kit.Store.FlushAsync(TestContext.Current.CancellationToken);
        Assert.True(kit.Store.TryGetChannel(s_bc, out var storedBc));
        var restarted = new GraphTestKit(kit.Repository, loadBatchSize: 1);
        var store = restarted.Store;
        var carolToBob = GraphTestKit.DirectionOf(new TestGossipKey(3), s_bob);
        var newerPolicy = new GraphPolicy(s_now + 10, 1, carolToBob, 40, 1, 990_000_000, 5, 5);
        var inMemoryBc = new GraphChannel(s_bc, storedBc.NodeId1, storedBc.NodeId2, storedBc.BitcoinKey1,
                                          storedBc.BitcoinKey2, 42);
        var changes = new List<string>();
        kit.Repository.BeforeStreamedRow = (stream, index) =>
        {
            switch (stream, index)
            {
                case ("channels", 1):
                    // ab's row is applied, bc's is not: remove ab, add bc with our own value and a newer policy
                    Assert.True(store.RemoveChannel(s_ab));
                    Assert.True(store.TryAddChannel(inMemoryBc));
                    Assert.True(store.TryApplyPolicy(s_bc, newerPolicy));
                    changes.Add("channels");
                    break;
                case ("nodes", 1):
                    // alice's row is applied, carol's is not
                    Assert.True(store.RemoveNode(s_alice.PubKey));
                    changes.Add("nodes");
                    break;
            }

            return Task.CompletedTask;
        };

        // Act
        await store.LoadAsync(TestContext.Current.CancellationToken);

        // Assert: the removed channel stays removed and its policy rows are skipped; the added channel keeps its
        // in-memory value, and its newer policy wins over the older row
        Assert.Equal(["channels", "nodes"], changes);
        Assert.True(store.IsLoaded);
        var snapshot = store.GetSnapshot();
        Assert.False(snapshot.TryGetChannel(s_ab, out _));
        Assert.True(snapshot.TryGetChannel(s_bc, out var bc));
        Assert.Equal(42UL, bc.CapacitySat);
        Assert.Equal(newerPolicy, bc.GetPolicy(carolToBob));
        Assert.Null(bc.GetPolicy((byte)(1 - carolToBob)));
        Assert.Equal(1, store.PolicyCount);
        var nodes = snapshot.Nodes.Select(n => n.NodeId).ToList();
        Assert.Equal([new TestGossipKey(3).PubKey], nodes);
        Assert.Equal(Recompute(snapshot), store.GetMemoryEstimate());
    }

    [Fact]
    public async Task Given_ChangesOfEveryKind_When_TheEstimateIsRead_Then_ItEqualsOneComputedFromTheSnapshot()
    {
        // Arrange
        var kit = await GraphStoreTests.CreateGraphAsync();
        var afterLoad = kit.Store.GetMemoryEstimate();

        // Act: a newer policy (replaces one), a spend, a newer node announcement, a removed channel and node
        var direction = GraphTestKit.DirectionOf(s_alice, s_bob);
        var update = GraphTestKit.SignedChannelUpdate(s_ab, s_alice, direction, s_now + 10, 7_777).Payload;
        Assert.True(kit.Store.TryApplyPolicy(s_ab, GraphPolicy.FromChannelUpdate(update) with
        {
            RawUpdate = update.GetBytes()
        }));
        Assert.True(kit.Store.MarkSpent(s_bc, 500));
        var announcement = GraphTestKit.SignedNodeAnnouncement(s_alice, s_now + 10, "alice-renamed").Payload;
        Assert.True(kit.Store.TryApplyNode(new GraphNode(announcement.NodeId, announcement.Timestamp,
                                                         announcement.Features, announcement.Alias.Span,
                                                         announcement.RgbColor.Span)
        {
            RawAnnouncement = announcement.GetBytes()
        }));
        var beforeRemoval = kit.Store.GetMemoryEstimate();
        Assert.True(kit.Store.RemoveChannel(s_bc));
        Assert.True(kit.Store.RemoveNode(new TestGossipKey(3).PubKey));

        // Assert
        Assert.Equal(Recompute(kit.Store.GetSnapshot()), kit.Store.GetMemoryEstimate());
        Assert.Equal(3, afterLoad.Policies);
        Assert.Equal(3, beforeRemoval.Policies);
        Assert.Equal(2, kit.Store.PolicyCount);
        Assert.True(kit.Store.GetMemoryEstimate().StoreBytes < beforeRemoval.StoreBytes);
    }

    [Fact]
    public void Given_AnEmptyStore_When_TheEstimateIsRead_Then_ItIsZero()
    {
        // Arrange
        var kit = new GraphTestKit();

        // Act
        var estimate = kit.Store.GetMemoryEstimate();

        // Assert
        Assert.Equal(new GraphMemoryEstimate(0, 0, 0, 0, 0, 0), estimate);
        Assert.Equal(0, estimate.TotalMegabytes);
    }

    [Fact]
    public void Given_AnEstimate_When_ItsTotalIsRead_Then_ItIsTheStorePlusOneSnapshotRoundedUpToAMegabyte()
    {
        // Arrange
        var estimate = GraphMemoryAccounting.Estimate(1_000, 2_000, 250, 300, 700_000);

        // Act & Assert: 700,000 + 1,000 x 950 + 2,000 x 250 + 250 x 1,000; 1,000 x 180 + 300 x 100
        Assert.Equal(2_400_000, estimate.StoreBytes);
        Assert.Equal(210_000, estimate.SnapshotBytes);
        Assert.Equal(2_610_000, estimate.TotalBytes);
        Assert.Equal(3, estimate.TotalMegabytes);
    }

    private static GraphMemoryEstimate Recompute(IGraphView snapshot)
    {
        var channels = snapshot.Channels.ToList();
        var nodes = snapshot.Nodes.ToList();
        var ends = channels.SelectMany(c => new[] { c.NodeId1, c.NodeId2 }).Distinct().Count();
        var variable = channels.Sum(GraphMemoryAccounting.VariableBytesOf)
                     + nodes.Sum(GraphMemoryAccounting.VariableBytesOf);
        return GraphMemoryAccounting.Estimate(channels.Count, channels.Sum(GraphMemoryAccounting.PolicyCountOf),
                                              nodes.Count, Math.Max(nodes.Count, ends), variable);
    }
}