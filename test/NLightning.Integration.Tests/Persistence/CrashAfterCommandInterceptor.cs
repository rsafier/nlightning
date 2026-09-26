using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Integration.Tests.Persistence;

/// <summary>
/// Throws <see cref="SimulatedCrashException"/> right after the k-th database command of a save has executed, i.e.
/// inside the save's transaction with the earlier statements already applied. Queries outside a save don't count.
/// </summary>
internal sealed class CrashAfterCommandInterceptor : DbCommandInterceptor, ISaveChangesInterceptor
{
    private int _crashAt;
    private int _executed;
    private bool _inSave;

    public void Arm(int crashAfterCommand)
    {
        _crashAt = crashAfterCommand;
        _executed = 0;
    }

    public void Disarm() => _crashAt = 0;

    public ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
                                                                 InterceptionResult<int> result,
                                                                 CancellationToken cancellationToken = default)
    {
        _inSave = true;
        return ValueTask.FromResult(result);
    }

    public ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
                                            CancellationToken cancellationToken = default)
    {
        _inSave = false;
        return ValueTask.FromResult(result);
    }

    public Task SaveChangesFailedAsync(DbContextErrorEventData eventData,
                                       CancellationToken cancellationToken = default)
    {
        _inSave = false;
        return Task.CompletedTask;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
                                                                 CommandExecutedEventData eventData,
                                                                 DbDataReader result,
                                                                 CancellationToken cancellationToken = default)
    {
        CountAndMaybeCrash(result);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
                                                         int result, CancellationToken cancellationToken = default)
    {
        CountAndMaybeCrash(null);
        return ValueTask.FromResult(result);
    }

    private void CountAndMaybeCrash(DbDataReader? reader)
    {
        if (!_inSave || _crashAt <= 0 || ++_executed != _crashAt)
            return;

        reader?.Dispose();
        throw new SimulatedCrashException(_executed);
    }
}