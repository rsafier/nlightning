using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace NLightning.Infrastructure.Repositories.Database.Helpers;

using Domain.Node.Fencing;

/// <summary>
/// Runs a database write that bypasses <see cref="UnitOfWork.SaveChangesAsync"/> (bulk <c>ExecuteUpdate</c>/
/// <c>ExecuteDelete</c>, a repository's own transaction) under the node write fence (NL-1342), the way the unit of work
/// runs its saves (NL-1341): inside one transaction, checked by <see cref="INodeWriteFence.CheckSaveAsync"/> on that
/// transaction's connection before the commit, so a refusal rolls the whole write back.
/// </summary>
/// <remarks>
/// A transaction the caller already opened on the context is the one checked, and it is not committed here. Without a
/// fence a write that needs no transaction of its own runs as before (no transaction is opened).
/// </remarks>
public static class FencedWrite
{
    /// <summary>Runs <paramref name="write"/> under the fence.</summary>
    /// <param name="context">The context the write goes through.</param>
    /// <param name="fence">The node write fence; null checks nothing.</param>
    /// <param name="write">The write; it must use <paramref name="context"/> only.</param>
    /// <param name="transactional">True when the write needs its own transaction even without a fence (several
    /// statements that commit together).</param>
    /// <param name="isolationLevel">The isolation level of a transaction opened here; the provider's default when
    /// null.</param>
    /// <param name="cancellationToken">Cancels the write and the check.</param>
    public static async Task<T> RunAsync<T>(DbContext context, INodeWriteFence? fence, Func<Task<T>> write,
                                            bool transactional = false, IsolationLevel? isolationLevel = null,
                                            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(write);

        var database = context.Database;
        if (database.CurrentTransaction is { } callerTransaction)
        {
            var result = await write();
            await CheckAsync(database, callerTransaction, fence, cancellationToken);
            return result;
        }

        if (fence is null && !transactional)
            return await write();

        await using var transaction = isolationLevel is { } level
                                          ? await database.BeginTransactionAsync(level, cancellationToken)
                                          : await database.BeginTransactionAsync(cancellationToken);
        var written = await write();
        await CheckAsync(database, transaction, fence, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return written;
    }

    /// <inheritdoc cref="RunAsync{T}"/>
    public static Task RunAsync(DbContext context, INodeWriteFence? fence, Func<Task> write,
                                bool transactional = false, IsolationLevel? isolationLevel = null,
                                CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        return RunAsync(context, fence, async () =>
        {
            await write();
            return true;
        }, transactional, isolationLevel, cancellationToken);
    }

    /// <summary>
    /// Asks the fence about the writes of the context's open <paramref name="transaction"/>, for a caller that runs
    /// its own transaction and writes only on some of its paths. Throws what the fence throws; the caller's transaction
    /// is then not committed (disposing it rolls it back).
    /// </summary>
    public static ValueTask CheckAsync(DatabaseFacade database, IDbContextTransaction transaction,
                                       INodeWriteFence? fence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(transaction);
        return fence is null
                   ? ValueTask.CompletedTask
                   : fence.CheckSaveAsync(database.GetDbConnection(), transaction.GetDbTransaction(),
                                          cancellationToken);
    }
}