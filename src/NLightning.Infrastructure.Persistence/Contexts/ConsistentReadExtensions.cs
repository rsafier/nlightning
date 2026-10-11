using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Persistence.Contexts;

/// <summary>
/// Runs a read made of several queries against one snapshot of the database (NL-810), so a save that commits between
/// two of its queries is seen either entirely or not at all. Without it each query of a multi-query load sees the
/// latest commit: a channel's row read before a splice lock and its commitment rows read after it gave a torn
/// channel ("Balances add up to 1100000000 msat, not 1000000000").
/// </summary>
/// <remarks>
/// <para>The transaction is read-only in use and never held across anything but the read's own queries; it never
/// blocks or deadlocks writers on PostgreSQL or SQL Server:</para>
/// <list type="bullet">
/// <item>PostgreSQL: <c>REPEATABLE READ</c>, a snapshot taken at the first query; a read-only transaction never fails
/// with a serialization error and takes no row locks.</item>
/// <item>SQLite: a deferred <c>BEGIN</c> (not <c>IMMEDIATE</c>, which would take the write lock): its shared lock keeps
/// the database unchanged until the read ends. In rollback-journal mode a writer's commit waits (busy retry) for it,
/// as it already waited for each single query; the reader holds no write lock, so it cannot deadlock.</item>
/// <item>SQL Server: <c>SNAPSHOT</c> isolation when the database allows it (<see cref="EnableSqlServerSnapshotIsolationAsync"/>,
/// run by the daemon's migration step); otherwise the read runs as before, query by query, because a locking
/// <c>REPEATABLE READ</c> could block writers and deadlock with them.</item>
/// </list>
/// <para>Inside a transaction the context already has (a caller's, or an outer consistent read) the read joins it.</para>
/// </remarks>
public static class ConsistentReadExtensions
{
    /// <summary>Whether each SQL Server database (by connection string) allows snapshot isolation.</summary>
    private static readonly ConcurrentDictionary<string, bool> s_sqlServerSnapshotAllowed = new();

    /// <summary>Runs <paramref name="read"/>, every query of it against one snapshot of the database.</summary>
    public static async Task<T> ReadConsistentlyAsync<T>(this DbContext context, Func<Task<T>> read)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(read);

        var database = context.Database;
        if (database.CurrentTransaction is not null || !database.IsRelational())
            return await read();

        if (database.IsSqlite())
            return await ReadSqliteAsync(database, read);

        IsolationLevel isolationLevel;
        if (database.IsNpgsql())
            isolationLevel = IsolationLevel.RepeatableRead;
        else if (database.IsSqlServer() && await IsSqlServerSnapshotAllowedAsync(database))
            isolationLevel = IsolationLevel.Snapshot;
        else
            return await read();

        await using var transaction = await database.BeginTransactionAsync(isolationLevel);
        var result = await read();
        await transaction.CommitAsync();
        return result;
    }

    /// <summary>
    /// Allows <c>SNAPSHOT</c> isolation on the SQL Server database, so <see cref="ReadConsistentlyAsync{T}"/> reads
    /// against a snapshot there. A no-op on the other providers. Must run outside a transaction (the daemon runs it
    /// after its migrations); needs <c>ALTER DATABASE</c> permission.
    /// </summary>
    public static async Task EnableSqlServerSnapshotIsolationAsync(this DbContext context,
                                                                   CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Database.IsSqlServer())
            return;

        await context.Database.ExecuteSqlRawAsync("ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON",
                                                  cancellationToken);
        if (context.Database.GetConnectionString() is { } connectionString)
            s_sqlServerSnapshotAllowed.TryRemove(connectionString, out _);
    }

    private static async Task<T> ReadSqliteAsync<T>(
        Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade database, Func<Task<T>> read)
    {
        await database.OpenConnectionAsync();
        try
        {
            SqliteTransaction transaction;
            try
            {
                transaction = ((SqliteConnection)database.GetDbConnection()).BeginTransaction(deferred: true);
            }
            catch (InvalidOperationException)
            {
                // A connection shared with another context that holds a transaction on it (only tests share one); its
                // statements run in that transaction, which already isolates them from other connections
                return await read();
            }

            await using (transaction)
            {
                await database.UseTransactionAsync(transaction);
                try
                {
                    var result = await read();
                    await transaction.CommitAsync();
                    return result;
                }
                finally
                {
                    await database.UseTransactionAsync(null);
                }
            }
        }
        finally
        {
            await database.CloseConnectionAsync();
        }
    }

    private static async Task<bool> IsSqlServerSnapshotAllowedAsync(
        Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade database)
    {
        var connectionString = database.GetConnectionString() ?? string.Empty;
        if (s_sqlServerSnapshotAllowed.TryGetValue(connectionString, out var allowed))
            return allowed;

        allowed = await database.SqlQueryRaw<int>(
                                    "SELECT CAST(snapshot_isolation_state AS int) AS [Value] FROM sys.databases "
                                  + "WHERE database_id = DB_ID()")
                                .FirstOrDefaultAsync() == 1;
        s_sqlServerSnapshotAllowed[connectionString] = allowed;
        return allowed;
    }
}