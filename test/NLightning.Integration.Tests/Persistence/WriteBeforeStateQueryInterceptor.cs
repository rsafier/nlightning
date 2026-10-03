using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace NLightning.Integration.Tests.Persistence;

/// <summary>
/// Counts the state loads of a channel read (each starts with the <c>Htlcs</c> query of
/// <c>ChannelStateDbRepository.LoadAsync</c>) and, before the first <paramref name="writes"/> of them, runs
/// <paramref name="sql"/> on the same connection: a save another unit of work commits between the channel row's query
/// and the state rows' queries (NL-805).
/// </summary>
internal sealed class WriteBeforeStateQueryInterceptor(string? sql, int writes) : DbCommandInterceptor
{
    public int StateLoads { get; private set; }

    public int Writes { get; private set; }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData,
                                                                     InterceptionResult<DbDataReader> result)
    {
        BeforeReader(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        BeforeReader(command);
        return ValueTask.FromResult(result);
    }

    private void BeforeReader(DbCommand command)
    {
        if (!command.CommandText.Contains("FROM \"Htlcs\"", StringComparison.Ordinal))
            return;

        StateLoads++;
        if (sql is null || Writes >= writes)
            return;

        Writes++;
        using var write = command.Connection!.CreateCommand();
        write.CommandText = sql;
        write.ExecuteNonQuery();
    }
}