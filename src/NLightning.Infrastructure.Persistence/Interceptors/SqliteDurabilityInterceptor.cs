using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace NLightning.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Sets <c>PRAGMA synchronous=FULL</c> on every SQLite connection EF opens, so a committed <c>SaveChanges</c> survives a
/// power loss (plan N5-T3): the channel state must be on disk before a message that depends on it is sent (invariant
/// I1). FULL is also SQLite's compiled-in default for rollback journals, but the setting is per connection and a
/// connection string or build can lower it, so it is forced here.
/// </summary>
public sealed class SqliteDurabilityInterceptor : DbConnectionInterceptor
{
    /// <summary>The statement run on every opened connection.</summary>
    public const string PragmaStatement = "PRAGMA synchronous=FULL;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = PragmaStatement;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
                                                     CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = PragmaStatement;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}