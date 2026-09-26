using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Integration.Tests.Persistence;

using Application.Gossip.Graph;
using Domain.Gossip.Graph;
using Infrastructure;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Repositories;

/// <summary>
/// BOLT 7 plan G5-T3 on SQLite (real migrations, unit of work and <c>GraphDbRepository</c>): a synthetic graph built
/// message by message in a <see cref="GraphStore"/>, written by its batched write-behind flush and loaded in bulk by a
/// new store, which must hold the same graph. The 200,000-channel run is the measurement (startup load under 10 s,
/// memory for <c>Gossip:MaxMemoryMb</c>); it is <c>Explicit</c> (<c>Category=Long</c>), a 5,000-channel run of the
/// same path runs by default.
/// </summary>
/// <remarks>
/// Recorded measurement (2026-09-26, Apple M3 Ultra, macOS, .NET 10.0.3 Release, workstation GC, SQLite file on the
/// local SSD), 200,000 channels / 400,000 policies / 50,000 nodes: startup load 1.55 s, batched flush of the whole
/// graph 19.4 s, first snapshot 69 ms, retained heap 475.0 MiB for the store and 39.1 MiB for a snapshot (estimate
/// 468.5 and 39.1 MiB, <see cref="GraphMemoryAccounting"/>), working set +336 MiB during the load. Run it with
/// <c>dotnet test test/NLightning.Integration.Tests -c Release -f net10.0 --filter "FullyQualifiedName~GraphStoreLoadPerformanceTests" -- xUnit.Explicit=only</c>.
/// </remarks>
[Collection(GraphStorePerformanceCollection.Name)]
public sealed class GraphStoreLoadPerformanceTests : IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"nltg-graph-perf-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
    }

    [Fact]
    public async Task Given_AGraphWrittenInBatches_When_ANewStoreLoadsItInBulk_Then_ItEqualsTheIncrementalGraph()
    {
        // Arrange: 5,000 channels, written in batches of 1,000 rows and loaded in batches of 777 (odd boundaries)
        var graph = SyntheticGossipGraph.Create(5_000, 1_250);

        // Act
        var result = await RunAsync(graph, writeBatchSize: 1_000, loadBatchSize: 777);

        // Assert
        AssertSameGraph(result.Written, result.Loaded);
        Assert.Equal(graph.Channels.Count, result.Loaded.ChannelCount);
        Assert.Equal(graph.Nodes.Count, result.Loaded.Nodes.Count());
        Assert.Equal(result.WrittenEstimate, result.LoadedEstimate);
        Assert.Equal(graph.PolicyCount, result.LoadedEstimate.Policies);
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Long")]
    public async Task Given_200kChannelsOnSqlite_When_TheStoreLoadsAtStartup_Then_ItTakesUnder10Seconds()
    {
        // Arrange: the plan's graph limit (MaxChannels 200,000), 50,000 nodes (about the mainnet ratio)
        var graph = SyntheticGossipGraph.Create(200_000, 50_000);

        // Act
        var result = await RunAsync(graph, GraphStore.DefaultWriteBatchSize, GraphStore.DefaultLoadBatchSize);

        // Assert
        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine(result.Describe());
        AssertSameGraph(result.Written, result.Loaded);
        Assert.Equal(result.WrittenEstimate, result.LoadedEstimate);
        Assert.True(result.LoadTime < TimeSpan.FromSeconds(10), result.Describe());

        // The estimate G5-T1 budgets with stays within 15 % of the measured heap (store and snapshot)
        Assert.InRange(result.LoadedEstimate.StoreBytes, result.StoreHeapBytes * 0.85, result.StoreHeapBytes * 1.15);
        Assert.InRange(result.LoadedEstimate.SnapshotBytes, result.SnapshotHeapBytes * 0.85,
                       result.SnapshotHeapBytes * 1.15);
    }

    private async Task<RunResult> RunAsync(SyntheticGossipGraph graph, int writeBatchSize, int loadBatchSize)
    {
        var ct = TestContext.Current.CancellationToken;

        // The incremental graph: every message added one at a time, then the write-behind flush
        IGraphView written;
        GraphMemoryEstimate writtenEstimate;
        TimeSpan flushTime;
        await using (var provider = await CreateProviderAsync(migrate: true))
        {
            var store = new GraphStore(provider.GetRequiredService<IServiceScopeFactory>(),
                                       NullLogger<GraphStore>.Instance)
            { WriteBatchSize = writeBatchSize };
            await store.LoadAsync(ct);
            graph.AddTo(store);
            var flush = Stopwatch.StartNew();
            await store.FlushAsync(ct);
            flushTime = flush.Elapsed;
            Assert.Equal(0, store.PendingChanges);
            written = store.GetSnapshot();
            writtenEstimate = store.GetMemoryEstimate();
        }

        SqliteConnection.ClearAllPools();

        // A restart: a new provider and store, the bulk load measured (time and retained managed memory)
        await using var restarted = await CreateProviderAsync(migrate: false);
        var loaded = new GraphStore(restarted.GetRequiredService<IServiceScopeFactory>(),
                                    NullLogger<GraphStore>.Instance)
        { LoadBatchSize = loadBatchSize };

        // Warm up EF's model and the connection so the timing is the load, not the first query's compilation
        using (var scope = restarted.CreateScope())
            _ = await scope.ServiceProvider.GetRequiredService<NLightningDbContext>().GraphNodes.CountAsync(ct);

        var heapBefore = MeasureHeap();
        var workingSetBefore = Environment.WorkingSet;
        var load = Stopwatch.StartNew();
        await loaded.LoadAsync(ct);
        var loadTime = load.Elapsed;
        var heapAfterLoad = MeasureHeap();
        var workingSetAfterLoad = Environment.WorkingSet;

        var snapshotTimer = Stopwatch.StartNew();
        var snapshot = loaded.GetSnapshot();
        var snapshotTime = snapshotTimer.Elapsed;
        var heapAfterSnapshot = MeasureHeap();
        GC.KeepAlive(loaded);

        return new RunResult(written, snapshot, writtenEstimate, loaded.GetMemoryEstimate(), flushTime, loadTime,
                             snapshotTime, heapAfterLoad - heapBefore, heapAfterSnapshot - heapAfterLoad,
                             workingSetAfterLoad - workingSetBefore, graph);
    }

    private async Task<ServiceProvider> CreateProviderAsync(bool migrate)
    {
        var configuration = new ConfigurationBuilder()
                           .AddInMemoryCollection(new Dictionary<string, string?>
                           {
                               ["Database:Provider"] = "sqlite",
                               ["Database:ConnectionString"] = $"Data Source={_databasePath}"
                           })
                           .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructureServices();
        services.AddPersistenceInfrastructureServices(configuration);
        services.AddRepositoriesInfrastructureServices();
        var provider = services.BuildServiceProvider();
        if (migrate)
        {
            using var scope = provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<NLightningDbContext>().Database
                       .MigrateAsync(TestContext.Current.CancellationToken);
        }

        return provider;
    }

    private static long MeasureHeap()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    private static void AssertSameGraph(IGraphView expected, IGraphView actual)
    {
        Assert.Equal(expected.ChannelCount, actual.ChannelCount);
        foreach (var channel in expected.Channels)
        {
            Assert.True(actual.TryGetChannel(channel.ShortChannelId, out var reloaded));

            // Equality covers the fields and both policies; the raw signed bytes are compared on their own
            Assert.Equal(channel, reloaded);
            Assert.True(channel.RawAnnouncement.Span.SequenceEqual(reloaded.RawAnnouncement.Span));
            Assert.True(channel.Policy1!.RawUpdate.Span.SequenceEqual(reloaded.Policy1!.RawUpdate.Span));
            Assert.True(channel.Policy2!.RawUpdate.Span.SequenceEqual(reloaded.Policy2!.RawUpdate.Span));
        }

        Assert.Equal(expected.Nodes.Count(), actual.Nodes.Count());
        foreach (var node in expected.Nodes)
        {
            Assert.True(actual.TryGetNode(node.NodeId, out var reloaded));
            Assert.Equal(node, reloaded);
            Assert.True(node.RawAnnouncement.Span.SequenceEqual(reloaded.RawAnnouncement.Span));
        }
    }

    private sealed record RunResult(
        IGraphView Written,
        IGraphView Loaded,
        GraphMemoryEstimate WrittenEstimate,
        GraphMemoryEstimate LoadedEstimate,
        TimeSpan FlushTime,
        TimeSpan LoadTime,
        TimeSpan SnapshotTime,
        long StoreHeapBytes,
        long SnapshotHeapBytes,
        long LoadWorkingSetBytes,
        SyntheticGossipGraph Graph)
    {
        public string Describe() =>
            string.Create(CultureInfo.InvariantCulture,
                          $"""
                           {Graph.Channels.Count} channels, {Graph.PolicyCount} policies, {Graph.Nodes.Count} nodes
                           flush (batched write-behind): {FlushTime.TotalSeconds:F2} s
                           startup load: {LoadTime.TotalSeconds:F2} s
                           first snapshot: {SnapshotTime.TotalMilliseconds:F0} ms
                           retained heap, store: {StoreHeapBytes / 1048576.0:F1} MiB ({StoreHeapBytes / (double)Graph.Channels.Count:F0} B per channel)
                           retained heap, snapshot: {SnapshotHeapBytes / 1048576.0:F1} MiB
                           working set growth during the load: {LoadWorkingSetBytes / 1048576.0:F1} MiB
                           estimate: store {LoadedEstimate.StoreBytes / 1048576.0:F1} MiB, snapshot {LoadedEstimate.SnapshotBytes / 1048576.0:F1} MiB, variable {LoadedEstimate.VariableBytes / 1048576.0:F1} MiB
                           """);
    }
}

/// <summary>The graph performance tests measure the process heap, so nothing else may run beside them.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GraphStorePerformanceCollection
{
    public const string Name = "GraphStorePerformance";
}