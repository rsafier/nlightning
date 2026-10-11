namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Graph;
using Domain.Gossip.Persistence;
using Infrastructure.Repositories.Database.Gossip;

/// <summary>
/// BOLT 7 plan G5-T3: the bulk members of <c>GraphDbRepository</c> on SQLite (real migrations). Batches span more
/// than one 500-key read, and they must stage exactly what the single-row members stage, staged and deleted rows of the
/// same unit of work included.
/// </summary>
public sealed class GraphDbRepositoryBulkTests : IDisposable
{
    private const int Count = 1_203;

    private readonly SqliteTestDatabase _database = new();
    private readonly SyntheticGossipGraph _graph = SyntheticGossipGraph.Create(Count, 300, seed: 11);

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task Given_MoreRowsThanOneRead_When_UpsertedInBulk_Then_TheyEqualTheSingleRowWrites()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var other = new SqliteTestDatabase();

        // Act: the same graph through the bulk members here and the single-row members there
        await using (var context = _database.CreateContext())
        {
            var repository = new GraphDbRepository(context);
            await repository.UpsertChannelsAsync(Channels(), ct);
            await repository.UpsertPoliciesAsync(Policies(), ct);
            await repository.UpsertNodesAsync(Nodes(), ct);
            await context.SaveChangesAsync(ct);
        }

        await using (var context = other.CreateContext())
        {
            var repository = new GraphDbRepository(context);
            foreach (var channel in Channels())
                await repository.UpsertChannelAsync(channel);
            foreach (var policy in Policies())
                await repository.UpsertPolicyAsync(policy);
            foreach (var node in Nodes())
                await repository.UpsertNodeAsync(node);
            await context.SaveChangesAsync(ct);
        }

        // Assert
        await using var bulkContext = _database.CreateContext();
        await using var singleContext = other.CreateContext();
        var bulk = new GraphDbRepository(bulkContext);
        var single = new GraphDbRepository(singleContext);
        AssertSameRows(await single.GetChannelsAsync(ct), await bulk.GetChannelsAsync(ct), c => c.ShortChannelId);
        AssertSameRows(await single.GetAllPoliciesAsync(ct), await bulk.GetAllPoliciesAsync(ct),
                       p => (p.ShortChannelId, p.Direction));
        AssertSameRows(await single.GetNodesAsync(ct), await bulk.GetNodesAsync(ct), n => n.NodeId);
        Assert.Equal(Count, (await bulk.GetChannelsAsync(ct)).Count);
    }

    [Fact]
    public async Task Given_StoredRows_When_UpsertedAgainInBulk_Then_EveryColumnIsReplacedWithoutDuplicates()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await SaveGraphAsync();
        var changedChannels = Channels().Select(c => c with { SpentAtHeight = 700_000, CapacitySat = 42 }).ToList();
        var changedPolicies = Policies().Select(p => p with { FeeBaseMsat = 9, Timestamp = p.Timestamp + 1 }).ToList();
        var changedNodes = Nodes().Select(n => new GraphNodeRecord(n.NodeId, n.Timestamp + 1, n.Features, n.Alias,
                                                                   [1, 2, 3], n.Addresses, n.RawAnnouncement,
                                                                   n.ReceivedAt)).ToList();

        // Act
        await using (var context = _database.CreateContext())
        {
            var repository = new GraphDbRepository(context);
            await repository.UpsertChannelsAsync(changedChannels, ct);
            await repository.UpsertPoliciesAsync(changedPolicies, ct);
            await repository.UpsertNodesAsync(changedNodes, ct);
            await context.SaveChangesAsync(ct);
        }

        // Assert
        await using var check = _database.CreateContext();
        var reader = new GraphDbRepository(check);
        AssertSameRows(changedChannels, await reader.GetChannelsAsync(ct), c => c.ShortChannelId);
        AssertSameRows(changedPolicies, await reader.GetAllPoliciesAsync(ct), p => (p.ShortChannelId, p.Direction));
        AssertSameRows(changedNodes, await reader.GetNodesAsync(ct), n => n.NodeId);
    }

    [Fact]
    public async Task Given_RowsStagedOrDeletedInTheSameUnitOfWork_When_UpsertedInBulk_Then_TheBulkValueIsSaved()
    {
        // Arrange: one channel and node staged by the single-row members, one channel and node deleted, all unsaved
        var ct = TestContext.Current.CancellationToken;
        await SaveGraphAsync();
        var channels = Channels();
        var nodes = Nodes();
        var staged = channels[0];
        var deleted = channels[1];
        var fresh = channels[^1] with { ShortChannelId = new ShortChannelId(900_000, 1, 0) };

        // Act
        await using (var context = _database.CreateContext())
        {
            var repository = new GraphDbRepository(context);
            await repository.UpsertChannelAsync(staged with { CapacitySat = 1 });
            await repository.UpsertChannelAsync(fresh);
            Assert.True(await repository.DeleteChannelAsync(deleted.ShortChannelId));
            Assert.True(await repository.DeleteNodeAsync(nodes[0].NodeId));
            await repository.UpsertChannelsAsync([staged with { CapacitySat = 2 }, deleted with { CapacitySat = 3 },
                                                  fresh with { CapacitySat = 4 }], ct);
            await repository.UpsertPoliciesAsync([Policies()[2] with { FeeBaseMsat = 5 }], ct);
            await repository.UpsertNodesAsync([nodes[0]], ct);
            await context.SaveChangesAsync(ct);
        }

        // Assert
        await using var check = _database.CreateContext();
        var reader = new GraphDbRepository(check);
        Assert.Equal(2UL, (await reader.GetChannelAsync(staged.ShortChannelId))!.CapacitySat);
        Assert.Equal(3UL, (await reader.GetChannelAsync(deleted.ShortChannelId))!.CapacitySat);
        Assert.Equal(4UL, (await reader.GetChannelAsync(fresh.ShortChannelId))!.CapacitySat);
        Assert.NotNull(await reader.GetNodeAsync(nodes[0].NodeId));
        Assert.Equal(5U, (await reader.GetPoliciesAsync(deleted.ShortChannelId)).Single(p => p.Direction == 0)
                                                                                 .FeeBaseMsat);
        Assert.Equal(Count + 1, (await reader.GetChannelsAsync(ct)).Count);
    }

    [Fact]
    public async Task Given_StoredChannelsAndNodes_When_DeletedInBulk_Then_TheirPoliciesGoTooAndUnknownKeysAreSkipped()
    {
        // Arrange: every other channel (more than one read) plus two unknown ids and one repeated id
        var ct = TestContext.Current.CancellationToken;
        await SaveGraphAsync();
        var doomed = Channels().Where((_, i) => i % 2 == 0).Select(c => c.ShortChannelId).ToList();
        var doomedNodes = Nodes().Take(10).Select(n => n.NodeId).ToList();
        var unknownBytes = new byte[33];
        unknownBytes[0] = 0x03;
        var unknownNode = new CompactPubKey(unknownBytes);

        // Act
        int deletedChannels, deletedNodes;
        await using (var context = _database.CreateContext())
        {
            var repository = new GraphDbRepository(context);
            deletedChannels = await repository.DeleteChannelsAsync(
                [.. doomed, new ShortChannelId(1, 1, 1), new ShortChannelId(2, 2, 2), doomed[0]], ct);
            deletedNodes = await repository.DeleteNodesAsync([.. doomedNodes, unknownNode], ct);
            await context.SaveChangesAsync(ct);
        }

        // Assert
        await using var check = _database.CreateContext();
        var reader = new GraphDbRepository(check);
        var channels = await reader.GetChannelsAsync(ct);
        var policies = await reader.GetAllPoliciesAsync(ct);
        Assert.Equal(doomed.Count, deletedChannels);
        Assert.Equal(doomedNodes.Count, deletedNodes);
        Assert.Equal(Count - doomed.Count, channels.Count);
        Assert.DoesNotContain(channels, c => doomed.Contains(c.ShortChannelId));
        Assert.Equal(2 * channels.Count, policies.Count);
        Assert.All(policies, p => Assert.Contains(channels, c => c.ShortChannelId == p.ShortChannelId));
        Assert.Equal(Nodes().Count - doomedNodes.Count, (await reader.GetNodesAsync(ct)).Count);
    }

    [Fact]
    public async Task Given_StoredGraph_When_Streamed_Then_EveryRowComesBackAsTheListReadsReturnIt()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await SaveGraphAsync();
        await using var context = _database.CreateContext();
        var repository = new GraphDbRepository(context);

        // Act
        var channels = await repository.StreamChannelsAsync(ct).ToListAsync(ct);
        var policies = await repository.StreamPoliciesAsync(ct).ToListAsync(ct);
        var nodes = await repository.StreamNodesAsync(ct).ToListAsync(ct);

        // Assert
        AssertSameRows(await repository.GetChannelsAsync(ct), channels, c => c.ShortChannelId);
        AssertSameRows(await repository.GetAllPoliciesAsync(ct), policies, p => (p.ShortChannelId, p.Direction));
        AssertSameRows(await repository.GetNodesAsync(ct), nodes, n => n.NodeId);
        Assert.Equal(2 * Count, policies.Count);
    }

    [Fact]
    public async Task Given_APolicyWithABadDirection_When_UpsertedInBulk_Then_NothingIsStaged()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var context = _database.CreateContext();
        var repository = new GraphDbRepository(context);
        var policies = Policies().Take(3).ToList();
        policies.Add(policies[0] with { Direction = 2 });

        // Act
        var exception = await Record.ExceptionAsync(() => repository.UpsertPoliciesAsync(policies, ct));

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(exception);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    private async Task SaveGraphAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = _database.CreateContext();
        var repository = new GraphDbRepository(context);
        await repository.UpsertChannelsAsync(Channels(), ct);
        await repository.UpsertPoliciesAsync(Policies(), ct);
        await repository.UpsertNodesAsync(Nodes(), ct);
        await context.SaveChangesAsync(ct);
    }

    private List<GraphChannelRecord> Channels() =>
        _graph.Channels.Select(c => ToRecord(c.Channel, c.FundingTxId)).ToList();

    private List<GraphPolicyRecord> Policies() =>
        _graph.Channels.SelectMany(c => new[] { c.Channel.Policy1!, c.Channel.Policy2! }
                                        .Select(p => ToRecord(c.Channel.ShortChannelId, p)))
              .ToList();

    private List<GraphNodeRecord> Nodes() =>
        _graph.Nodes.Select(n => new GraphNodeRecord(n.NodeId, n.Timestamp, n.Features.ToArray(), n.Alias.ToArray(),
                                                     n.RgbColor.ToArray(), [0x01, 203, 0, 113, 7, 0x26, 0x07],
                                                     n.RawAnnouncement.ToArray(), s_receivedAt))
              .ToList();

    private static readonly DateTimeOffset s_receivedAt = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static GraphChannelRecord ToRecord(GraphChannel channel, TxId fundingTxId) =>
        new(channel.ShortChannelId, channel.NodeId1, channel.NodeId2, channel.BitcoinKey1, channel.BitcoinKey2,
            channel.CapacitySat!.Value, channel.Features.ToArray(), channel.RawAnnouncement.ToArray(),
            Domain.Gossip.Persistence.GraphChannelVerification.Verified, null, s_receivedAt, fundingTxId);

    private static GraphPolicyRecord ToRecord(ShortChannelId shortChannelId, GraphPolicy policy) =>
        new(shortChannelId, policy.Direction, policy.Timestamp, policy.MessageFlags, policy.ChannelFlags,
            policy.CltvExpiryDelta, policy.HtlcMinimumMsat, policy.HtlcMaximumMsat, policy.FeeBaseMsat,
            policy.FeeProportionalMillionths, policy.RawUpdate.ToArray());

    /// <summary>Same keys, and every column equal (records compare their arrays by reference, so by value here).</summary>
    private static void AssertSameRows<T, TKey>(IEnumerable<T> expected, IEnumerable<T> actual, Func<T, TKey> keyOf)
        where TKey : notnull
    {
        var byKey = actual.ToDictionary(keyOf);
        var expectedList = expected.ToList();
        Assert.Equal(expectedList.Count, byKey.Count);
        foreach (var row in expectedList)
        {
            Assert.True(byKey.TryGetValue(keyOf(row), out var other), $"missing {keyOf(row)}");
            Assert.Equal(Describe(row), Describe(other));
        }
    }

    private static string Describe<T>(T row) =>
        string.Join('|', typeof(T).GetProperties()
                                  .Where(p => p.Name != "EqualityContract")
                                  .Select(p => p.GetValue(row) switch
                                   {
                                       byte[] bytes => Convert.ToHexString(bytes),
                                       var value => value?.ToString() ?? "null"
                                   }));
}