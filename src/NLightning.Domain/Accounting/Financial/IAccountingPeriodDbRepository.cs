namespace NLightning.Domain.Accounting.Financial;

/// <summary>
/// The accounting periods and their closes (<c>AccountingPeriods</c>, A3-T5). Writes are staged and committed by the
/// unit of work's save.
/// </summary>
public interface IAccountingPeriodDbRepository
{
    /// <summary>Stages a new period; throws <see cref="InvalidOperationException"/> when its id exists.</summary>
    Task AddAsync(AccountingPeriod period, CancellationToken cancellationToken = default);

    /// <summary>Stages every field of an existing period but its id; throws <see cref="InvalidOperationException"/>
    /// when there is none.</summary>
    Task UpdateAsync(AccountingPeriod period, CancellationToken cancellationToken = default);

    /// <summary>The period (saved or staged), or null.</summary>
    Task<AccountingPeriod?> GetAsync(string periodId, CancellationToken cancellationToken = default);

    /// <summary>Every saved period, oldest first (by start).</summary>
    Task<IReadOnlyList<AccountingPeriod>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>The closed period that ends last, or null before the first close.</summary>
    Task<AccountingPeriod?> GetLastClosedAsync(CancellationToken cancellationToken = default);

    /// <summary>The closed period that holds <paramref name="time"/> (start ≤ time &lt; end), or null: the lock's
    /// check (D-A8).</summary>
    Task<AccountingPeriod?> GetClosedContainingAsync(DateTimeOffset time,
                                                     CancellationToken cancellationToken = default);
}