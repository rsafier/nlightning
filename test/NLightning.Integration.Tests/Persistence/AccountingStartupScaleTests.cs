using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Integration.Tests.Persistence;

using Application.Accounting;
using Application.Accounting.Backfill;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Persistence.Interfaces;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Providers;
using Infrastructure.Repositories;
using Infrastructure.Repositories.Database.Accounting;
using Infrastructure.Repositories.Memory;

/// <summary>
/// The flat-startup proof of the accounting feed (NL-602 A1-T6, plan <c>docs/agents/ACCOUNTING_PLAN.md</c> §10): what a
/// start reads from <c>AccountingEvents</c> (the cutover marker lookup, the sealer's empty round: the unsealed batch and
/// the chain tip) is answered from an index on SQLite, never by a scan of the table, so its cost does not grow with the
/// history. The default test checks the query plans on 50,000 sealed rows; the <c>Explicit</c> <c>Long</c> test times the
/// startup path on 1,000,000 rows against 10,000.
/// </summary>
public sealed class AccountingStartupScaleTests : IDisposable
{
    private const int Rounds = 25;

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"nltg-accounting-scale-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Given_50000SealedRows_When_TheStartupQueriesRun_Then_EveryOneUsesAnIndex()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenDatabaseAsync(ct);
        await BulkInsertAsync(connection, sealedRows: 50_000, unsealedRows: 25, duplicateRows: 25, ct);
        var capture = new CommandCapture();

        // Act: the startup reads, through the production repository
        await using (var context = CreateContext(connection, capture))
        {
            var repository = new AccountingEventDbRepository(context);
            Assert.True(await repository.ExistsAsync(AccountingEventKeys.Cutover(), ct));
            Assert.Equal(50_000, (await repository.GetChainTipAsync(ct)).LedgerSeq);
            Assert.Equal(25, (await repository.GetUnsealedAsync(500, ct)).Count);
        }

        // Assert: one command each, every one answered from an index
        Assert.Equal(3, capture.Commands.Count);
        foreach (var command in capture.Commands)
        {
            var plan = await ExplainAsync(connection, command, ct);
            TestContext.Current.TestOutputHelper?.WriteLine($"{command.Text}\n  -> {string.Join(" | ", plan)}");
            // "SCAN CONSTANT ROW" is the EXISTS wrapper; a scan of the table (aliased "a" by EF) is what must not occur
            Assert.DoesNotContain(plan, d => d.StartsWith("SCAN a", StringComparison.Ordinal)
                                          || d.StartsWith("SCAN AccountingEvents", StringComparison.Ordinal));
            Assert.DoesNotContain(plan, d => d.Contains("TEMP B-TREE", StringComparison.Ordinal));
            Assert.Contains(plan, d => d.Contains("USING INDEX", StringComparison.Ordinal)
                                    || d.Contains("USING COVERING INDEX", StringComparison.Ordinal));
        }
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Long")]
    public async Task Given_1000000Rows_When_TheNodeStarts_Then_TheFeedsStartupCostStaysFlat()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var small = await MeasureStartupAsync(10_000, ct);
        var large = await MeasureStartupAsync(1_000_000, ct);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"startup path (marker lookup + empty seal round), median of {Rounds}: 10,000 rows {small.TotalMilliseconds:F2} ms, "
          + $"1,000,000 rows {large.TotalMilliseconds:F2} ms");

        // Assert: generous bounds; a scan of a million rows takes hundreds of milliseconds on SQLite
        Assert.True(large < TimeSpan.FromMilliseconds(100), $"1,000,000 rows: {large.TotalMilliseconds} ms");
        Assert.True(large.TotalMilliseconds <= small.TotalMilliseconds * 5 + 5,
                    $"1,000,000 rows {large.TotalMilliseconds} ms against 10,000 rows {small.TotalMilliseconds} ms");
    }

    public void Dispose()
    {
        SqliteTestPools.Clear(_databasePath);
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A leftover temp file is harmless
            }
        }
    }

    /// <summary>
    /// The median time of the feed's part of a start on a database holding <paramref name="rows"/> sealed rows and the
    /// cutover marker: the backfill's marker lookup and an empty sealer round, each in its own scope as at startup.
    /// </summary>
    private async Task<TimeSpan> MeasureStartupAsync(int rows, CancellationToken ct)
    {
        var path = _databasePath + $".{rows}";
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync(ct);
                await using (var context = CreateContext(connection))
                    await context.Database.MigrateAsync(ct);
                await BulkInsertAsync(connection, rows, 0, 0, ct);
            }

            var services = new ServiceCollection();
            services.AddScoped<IUnitOfWork>(_ => new UnitOfWork(CreateContext($"Data Source={path}"),
                                                                 NullLogger<UnitOfWork>.Instance, new Sha256(),
                                                                 new UtxoMemoryRepository()));
            await using var provider = services.BuildServiceProvider();
            var scopes = provider.GetRequiredService<IServiceScopeFactory>();
            await using var backfill = new AccountingBackfillService(scopes, NullLogger<AccountingBackfillService>.Instance);
            await using var sealer = new AccountingEventSealerService(scopes,
                                                                      NullLogger<AccountingEventSealerService>.Instance);

            var times = new List<TimeSpan>();
            for (var i = 0; i < Rounds + 3; i++)
            {
                var watch = Stopwatch.StartNew();
                var cutover = await backfill.EnsureCutoverAsync(ct);
                var round = await sealer.SealNowAsync(ct);
                watch.Stop();
                Assert.Equal(AccountingCutoverOutcome.AlreadyDone, cutover.Outcome);
                Assert.Equal(0, round.Sealed);
                Assert.Equal(rows, round.Tip.LedgerSeq);
                if (i >= 3)
                    times.Add(watch.Elapsed);
            }

            times.Sort();
            return times[times.Count / 2];
        }
        finally
        {
            SqliteTestPools.Clear(path);
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(file))
                    File.Delete(file);
        }
    }

    private async Task<SqliteConnection> OpenDatabaseAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(ct);
        await using var context = CreateContext(connection);
        await context.Database.MigrateAsync(ct);
        return connection;
    }

    private static NLightningDbContext CreateContext(DbConnection connection, params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<NLightningDbContext>()
           .UseSqlite(connection, o => o.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"));
        if (interceptors.Length > 0)
            builder.AddInterceptors(interceptors);
        return new NLightningDbContext(builder.Options, new DatabaseTypeProvider(DatabaseType.Sqlite));
    }

    private static NLightningDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<NLightningDbContext>()
           .UseSqlite(connectionString, o => o.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
           .Options, new DatabaseTypeProvider(DatabaseType.Sqlite));

    /// <summary>
    /// Inserts the cutover marker and <paramref name="sealedRows"/> sealed rows (ledger sequence 1 to n with a hash),
    /// then unsealed rows and rows marked duplicate, in one transaction.
    /// </summary>
    private static async Task BulkInsertAsync(SqliteConnection connection, int sealedRows, int unsealedRows,
                                              int duplicateRows, CancellationToken ct)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO AccountingEvents (EventKey, Kind, OccurredAt, AmountMsat, FeeMsat, Finality, Flags, LedgerSeq, Hash)
            VALUES ($key, $kind, $at, $amount, 0, 0, $flags, $seq, $hash)
            """;
        var key = command.Parameters.Add("$key", SqliteType.Text);
        var kind = command.Parameters.Add("$kind", SqliteType.Integer);
        var at = command.Parameters.Add("$at", SqliteType.Integer);
        var amount = command.Parameters.Add("$amount", SqliteType.Integer);
        var flags = command.Parameters.Add("$flags", SqliteType.Integer);
        var seq = command.Parameters.Add("$seq", SqliteType.Integer);
        var hash = command.Parameters.Add("$hash", SqliteType.Blob);
        await command.PrepareAsync(ct);

        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var hashBytes = new byte[32];
        for (var i = 1; i <= sealedRows; i++)
        {
            var isMarker = i == 1;
            key.Value = isMarker ? AccountingEventKeys.Cutover() : $"bulk:{i}";
            kind.Value = isMarker ? (int)AccountingEventKind.OpeningBalance : (int)AccountingEventKind.ForwardSettled;
            at.Value = start + i * TimeSpan.TicksPerSecond;
            amount.Value = isMarker ? 0 : i;
            flags.Value = isMarker ? (int)AccountingEventFlags.Backfilled : 0;
            seq.Value = i;
            hashBytes[0] = (byte)i;
            hashBytes[1] = (byte)(i >> 8);
            hashBytes[2] = (byte)(i >> 16);
            hash.Value = hashBytes;
            await command.ExecuteNonQueryAsync(ct);
        }

        for (var i = 0; i < unsealedRows + duplicateRows; i++)
        {
            var duplicate = i >= unsealedRows;
            key.Value = duplicate ? $"bulk:{i + 2}" : $"unsealed:{i}";
            kind.Value = (int)AccountingEventKind.ForwardSettled;
            at.Value = start + (sealedRows + i + 1) * TimeSpan.TicksPerSecond;
            amount.Value = i;
            flags.Value = duplicate ? (int)AccountingEventFlags.Duplicate : 0;
            seq.Value = DBNull.Value;
            hash.Value = DBNull.Value;
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>The <c>EXPLAIN QUERY PLAN</c> details of a captured command, run with its parameters.</summary>
    private static async Task<List<string>> ExplainAsync(SqliteConnection connection, CapturedCommand captured,
                                                         CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + captured.Text;
        foreach (var (name, value) in captured.Parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);

        var details = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            details.Add(reader.GetString(reader.GetOrdinal("detail")));
        return details;
    }

    private sealed record CapturedCommand(string Text, IReadOnlyList<(string Name, object? Value)> Parameters);

    /// <summary>Records the text and parameters of every query EF sends.</summary>
    private sealed class CommandCapture : DbCommandInterceptor
    {
        public List<CapturedCommand> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData,
                                                                         InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        private void Record(DbCommand command) =>
            Commands.Add(new CapturedCommand(command.CommandText,
                                             command.Parameters.Cast<DbParameter>()
                                                    .Select(p => (p.ParameterName, (object?)p.Value))
                                                    .ToList()));
    }
}