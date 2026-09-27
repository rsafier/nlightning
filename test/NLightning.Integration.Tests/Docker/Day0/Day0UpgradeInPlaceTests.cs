using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NLightning.Integration.Tests.Docker.Day0;

using Abcd;
using Domain.Bitcoin.Enums;
using Domain.Channels.Enums;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Money;
using Fixtures;
using Gossip;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Providers;
using Utils;

/// <summary>
/// The upgrade in place of the day-0 runbook (wave sp2 lane SP2-F, <c>docs/agents/DAY0_RUNBOOK.md</c> "Mutinynet
/// phase"): a node whose SQLite database has the schema of the builds before wave sp1 (the migration before
/// <c>AddSpliceFundings</c>) with a v1 channel in it starts on the new build, migrates at startup, reestablishes the
/// channel with its peer, pays both ways on it and then splices it.
/// </summary>
/// <remarks>
/// <para>How the old database is made: a channel a peer would accept cannot be written by hand (its keys,
/// commitments and signatures must match the peer's), so the two nodes open and use a real v1 channel with the
/// features of a pre-sp1 build (<see cref="Day0Harness.UsePreSp1Features"/>: no quiescence, splicing or dual
/// funding), stop, and each database is rolled back with EF's migrator to the migration before
/// <c>AddSpliceFundings</c> (its hand-written <c>Down</c> keeps the channel's rows, <c>SpliceFundingsSchemaRoundTrip</c>
/// proves that step on seeded rows). The test checks the rolled-back file really has the old schema (no
/// <c>ChannelFundings</c> table, no <c>Commitments.FundingTxId</c>) before the upgraded start.</para>
/// <para>The splice after the upgrade needs the SP2 lanes (lock and SCID switch, SP2-B); the integrator runs it after
/// the merge, in the gossip collection's process:
/// <c>scripts/run-gossip.sh 1 Release -class NLightning.Integration.Tests.Docker.Day0.Day0UpgradeInPlaceTests</c>. The
/// channel is private and the test needs no LND node, but the fixture's bitcoind.</para>
/// </remarks>
[Collection(GossipRegtestCollection.Name)]
public sealed class Day0UpgradeInPlaceTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 20 * 60 * 1_000;
    private const string SpliceFundingsMigration = "_AddSpliceFundings";
    private const string SqliteMigrationsAssembly = "NLightning.Infrastructure.Persistence.Sqlite";

    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);
    private static readonly LightningMoney s_push = LightningMoney.Satoshis(300_000);
    private const ulong SpliceInSat = 100_000;

    private readonly LightningRegtestNetworkFixture _fixture;
    private readonly List<NLightningTestNode> _nodes = [];

    public Day0UpgradeInPlaceTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (DockerDiagnostics.CurrentTestFailed)
        {
            foreach (var node in _nodes)
            {
                Console.WriteLine($"===== {node.Name}: last log lines =====");
                foreach (var line in node.NodeLog.TakeLast(300))
                    Console.WriteLine(line);
            }
        }

        foreach (var node in _nodes)
            await node.DisposeAsync();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_AV1ChannelInAPreSp1Database_When_TheNodesUpgradeInPlace_Then_ItMigratesAtStartupWorksAndSplices()
    {
        // Arrange: two nodes as a pre-sp1 build runs them, a used v1 channel between them
        var ct = TestContext.Current.CancellationToken;
        var a = await CreateNodeAsync("upgrade-a", ct);
        var b = await CreateNodeAsync("upgrade-b", ct);
        await a.FundWalletAsync(LightningMoney.Satoshis(1_500_000), AddressType.P2Wpkh, ct);
        await a.FundWalletAsync(LightningMoney.Satoshis(500_000), AddressType.P2Wpkh, ct);
        // B accepts an anchors channel only with its on-chain reserve (NL-379)
        await b.FundWalletAsync(LightningMoney.Satoshis(200_000), AddressType.P2Wpkh, ct);
        await Day0Harness.ConnectBothWaysAsync(a, b, ct);
        var opened = await a.OpenChannelAsync(new OpenChannelClientRequest(b.Address, s_capacity)
        {
            PushAmount = s_push
        }, ct);
        var channelId = opened.ChannelId;
        var (usableA, _) = await Day0Harness.MineUntilUsableAsync(_fixture, [], a, b, channelId, ct);
        Assert.Equal(ChannelVersion.V1, Channel(a, channelId).Version);
        await Day0Harness.PayAsync(a, b, 40_000, "upgrade before a->b", ct);
        await Day0Harness.PayAsync(b, a, 10_000, "upgrade before b->a", ct);
        var beforeA = await Day0Harness.WaitSettledAsync(a, channelId, ct);
        var beforeB = await Day0Harness.WaitSettledAsync(b, channelId, ct);
        Assert.NotNull(beforeA.FundingTxId);
        Console.WriteLine($"[upgrade] before: {beforeA.Describe()} funding {Day0Harness.Display(beforeA.FundingTxId)}"
                        + $" (opened at scid {usableA.ShortChannelId})");

        // ...stopped, and each database rolled back to the schema before AddSpliceFundings
        await a.StopAsync();
        await b.StopAsync();
        SqliteConnection.ClearAllPools();
        var rowsBefore = new Dictionary<string, IReadOnlyDictionary<string, int>>();
        foreach (var node in new[] { a, b })
        {
            rowsBefore[node.Name] = await CountChannelRowsAsync(node, "before the rollback", ct);
            var preSp1 = await RollBackToPreSp1Async(node, ct);
            await AssertPreSp1SchemaAsync(node, preSp1, ct);
            Assert.Equal(rowsBefore[node.Name], await CountChannelRowsAsync(node, "at the pre-sp1 schema", ct));
        }

        SqliteConnection.ClearAllPools();

        // Act: the new build with the runbook's features starts on the old files (migrate at startup)
        foreach (var node in new[] { a, b })
        {
            Day0Harness.EnableDay0Features(node);
            await node.StartAsync(ct);
        }

        // Assert: migrated, the channel's initial funding row written, the commitments under that funding
        foreach (var (node, before) in new[] { (a, beforeA), (b, beforeB) })
        {
            await AssertMigratedAsync(node, before, ct);
            Assert.Equal(rowsBefore[node.Name], await CountChannelRowsAsync(node, "after the upgrade", ct));
        }

        // ...the channel reestablishes with unchanged balances and commitment numbers and pays both ways
        await Day0Harness.EnsureConnectedAsync(a, b, ct);
        var afterA = await Day0Harness.WaitSettledAsync(a, channelId, ct);
        var afterB = await Day0Harness.WaitSettledAsync(b, channelId, ct);
        foreach (var (before, after) in new[] { (beforeA, afterA), (beforeB, afterB) })
        {
            Assert.Equal(before.FundingTxId, after.FundingTxId);
            Assert.Equal(before.Capacity, after.Capacity);
            Assert.Equal(before.LocalBalance, after.LocalBalance);
            Assert.Equal(before.RemoteBalance, after.RemoteBalance);
            Assert.Equal(before.LocalCommitmentNumber, after.LocalCommitmentNumber);
            Assert.Equal(before.RemoteCommitmentNumber, after.RemoteCommitmentNumber);
            Assert.Equal(before.ShortChannelId?.ToUInt64(), after.ShortChannelId?.ToUInt64());
            Assert.False(after.DataLossDetected);
        }

        await Day0Harness.PayAsync(a, b, 20_000, "upgrade after a->b", ct);
        await Day0Harness.PayAsync(b, a, 5_000, "upgrade after b->a", ct);
        await Day0Harness.BackupAsync(a, await Day0Harness.WaitSettledAsync(a, channelId, ct), "upgrade", ct);
        await Day0Harness.BackupAsync(b, await Day0Harness.WaitSettledAsync(b, channelId, ct), "upgrade", ct);

        // ...and splices: A splices in, the splice locks on both ends, payments both ways on the new funding
        var beforeSplice = await Day0Harness.WaitSettledAsync(a, channelId, ct);
        var splice = await Day0Harness.SpliceInAsync(a, channelId, SpliceInSat, ct);
        var spliceTxId = Day0Harness.AssertSigned(splice);
        var (lockedA, lockedB) = await Day0Harness.MineUntilSpliceLockedAsync(_fixture, [], a, b, channelId, spliceTxId,
                                                                              ct);
        Assert.Equal(beforeSplice.Capacity.Satoshi + (long)SpliceInSat, lockedA.Capacity.Satoshi);
        Assert.Equal(beforeSplice.LocalBalance.MilliSatoshi + SpliceInSat * 1_000, lockedA.LocalBalance.MilliSatoshi);
        Assert.Equal(lockedA.Capacity, lockedB.Capacity);
        Assert.NotEqual(beforeSplice.ShortChannelId?.ToUInt64(), lockedA.ShortChannelId?.ToUInt64());
        await Day0Harness.PayAsync(a, b, 15_000, "upgrade spliced a->b", ct);
        await Day0Harness.PayAsync(b, a, 7_000, "upgrade spliced b->a", ct);
        await Day0Harness.BackupAsync(a, lockedA, "upgrade splice", ct);
        await Day0Harness.BackupAsync(b, lockedB, "upgrade splice", ct);
    }

    private async Task<NLightningTestNode> CreateNodeAsync(string name, CancellationToken ct)
    {
        var node = await NLightningTestNode.CreateAsync(_fixture, name);
        _nodes.Add(node);
        Day0Harness.UsePreSp1Features(node);
        await node.StartAsync(ct);
        return node;
    }

    /// <summary>
    /// Rolls <paramref name="node"/>'s (stopped) SQLite database back to the migration before
    /// <c>AddSpliceFundings</c>; returns that migration's id.
    /// </summary>
    private static async Task<string> RollBackToPreSp1Async(NLightningTestNode node, CancellationToken ct)
    {
        await using var context = CreateContext(node);
        var migrations = context.Database.GetMigrations().ToList();
        var spliceFundings = migrations.Single(m => m.EndsWith(SpliceFundingsMigration, StringComparison.Ordinal));
        var preSp1 = migrations[migrations.IndexOf(spliceFundings) - 1];
        Console.WriteLine($"[upgrade] {node.Name}: rolling {node.DatabaseFilePath} back to {preSp1}");
        await context.GetService<IMigrator>().MigrateAsync(preSp1, ct);
        return preSp1;
    }

    /// <summary>The database is at <paramref name="preSp1"/> and has none of <c>AddSpliceFundings</c>' schema.</summary>
    private static async Task AssertPreSp1SchemaAsync(NLightningTestNode node, string preSp1, CancellationToken ct)
    {
        await using var context = CreateContext(node);
        var applied = (await context.Database.GetAppliedMigrationsAsync(ct)).ToList();
        Assert.Equal(preSp1, applied[^1]);
        Assert.Contains(await context.Database.GetPendingMigrationsAsync(ct),
                        m => m.EndsWith(SpliceFundingsMigration, StringComparison.Ordinal));
        Assert.Equal(0, await CountAsync(context,
                                         "SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type = 'table' "
                                       + "AND name = 'ChannelFundings'", ct));
        Assert.Equal(0, await CountAsync(context,
                                         "SELECT COUNT(*) AS \"Value\" FROM pragma_table_info('Commitments') "
                                       + "WHERE name = 'FundingTxId'", ct));
        Assert.Equal(1, await CountAsync(context, "SELECT COUNT(*) AS \"Value\" FROM \"Channels\"", ct));
        Assert.True(await CountAsync(context, "SELECT COUNT(*) AS \"Value\" FROM \"Commitments\"", ct) > 0,
                    $"{node.Name}'s rolled-back database lost the channel's commitments");
    }

    /// <summary>
    /// Every migration applied, and the channel has its <see cref="ChannelFundingKind.Initial"/>,
    /// <see cref="ChannelFundingStatus.Current"/> funding row on its funding outpoint with the commitments under it.
    /// </summary>
    private static async Task AssertMigratedAsync(NLightningTestNode node,
                                                  Domain.Client.Responses.ChannelInfoClientResponse before,
                                                  CancellationToken ct)
    {
        await using var context = CreateContext(node);
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(ct));
        var channelId = before.ChannelId;
        var funding = Assert.Single(await context.ChannelFundings.AsNoTracking()
                                                 .Where(f => f.ChannelId == channelId).ToListAsync(ct));
        Assert.Equal(before.FundingTxId, funding.FundingTxId);
        Assert.Equal(before.FundingOutputIndex, funding.OutputIndex);
        Assert.Equal(before.Capacity.Satoshi, funding.CapacitySatoshis);
        Assert.Equal((byte)ChannelFundingKind.Initial, funding.Kind);
        Assert.Equal((byte)ChannelFundingStatus.Current, funding.Status);
        Assert.Equal(0u, funding.LocalFundingKeyIndex);
        var commitments = await context.Commitments.AsNoTracking().Where(c => c.ChannelId == channelId)
                                       .ToListAsync(ct);
        Assert.NotEmpty(commitments);
        Assert.All(commitments, c => Assert.Equal(before.FundingTxId, c.FundingTxId));
        Console.WriteLine($"[upgrade] {node.Name}: migrated at startup, funding row {Day0Harness.Display(funding.FundingTxId)}"
                        + $":{funding.OutputIndex}, {commitments.Count} commitment slot(s)");

        // The channel loads as ChannelDbRepository.GetByIdAsync reads it (the channel with its config, key sets, change
        // address and aliases)
        var loaded = await context.Channels.AsNoTracking()
                                  .Include(c => c.Config)
                                  .Include(c => c.KeySets)
                                  .Include(c => c.ChangeAddress)
                                  .Include(c => c.LocalAliases)
                                  .FirstOrDefaultAsync(c => c.ChannelId == channelId, ct);
        Assert.NotNull(loaded);
        Assert.NotNull(loaded.Config);
        Assert.Equal(2, loaded.KeySets?.Count);
    }

    /// <summary>
    /// The rows of the channel tables every schema since wave qit has (the rollback and the upgrade must keep them
    /// all), printed with <paramref name="label"/>.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, int>> CountChannelRowsAsync(NLightningTestNode node,
        string label, CancellationToken ct)
    {
        string[] tables =
        [
            "Peers", "Channels", "ChannelConfigs", "ChannelKeySets", "ChannelLocalAliases", "Commitments",
            "RevokedCommitments", "RemoteShachains"
        ];
        await using var context = CreateContext(node);
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var table in tables)
            counts[table] = await CountAsync(context, $"SELECT COUNT(*) AS \"Value\" FROM \"{table}\"", ct);
        Console.WriteLine($"[upgrade] {node.Name} rows {label}: "
                        + string.Join(", ", counts.Select(c => $"{c.Key} {c.Value}")));
        return counts;
    }

    private static NLightningDbContext CreateContext(NLightningTestNode node)
    {
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseSqlite(node.Database.ConnectionString, x => x.MigrationsAssembly(SqliteMigrationsAssembly))
                     .Options;
        return new NLightningDbContext(options, new DatabaseTypeProvider(DatabaseType.Sqlite));
    }

    private static Task<int> CountAsync(NLightningDbContext context, string sql, CancellationToken ct) =>
        context.Database.SqlQueryRaw<int>(sql).SingleAsync(ct);

    private static Domain.Channels.Models.ChannelModel Channel(NLightningTestNode node, ChannelId channelId) =>
        node.ChannelMemoryRepository.TryGetChannel(channelId, out var channel)
            ? channel
            : throw new InvalidOperationException($"{node.Name} has no channel {channelId}");
}