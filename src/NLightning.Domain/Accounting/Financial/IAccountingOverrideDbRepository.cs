namespace NLightning.Domain.Accounting.Financial;

/// <summary>
/// The manual reclassifications (<c>AccountingOverrides</c>, A3-T3), one per event key. Writes are staged and committed
/// by the unit of work's save.
/// </summary>
public interface IAccountingOverrideDbRepository
{
    /// <summary>Stages the override of its event key, replacing one that exists (saved or staged).</summary>
    Task SetAsync(AccountingOverride accountingOverride, CancellationToken cancellationToken = default);

    /// <summary>The override of an event key (saved or staged), or null.</summary>
    Task<AccountingOverride?> GetAsync(string eventKey, CancellationToken cancellationToken = default);

    /// <summary>The saved overrides of these event keys, by key.</summary>
    Task<IReadOnlyDictionary<string, AccountingOverride>> GetManyAsync(IReadOnlyCollection<string> eventKeys,
                                                                       CancellationToken cancellationToken = default);

    /// <summary>Saved overrides by event key, at most <paramref name="take"/> after <paramref name="skip"/>.</summary>
    Task<IReadOnlyList<AccountingOverride>> ListAsync(int skip, int take,
                                                      CancellationToken cancellationToken = default);

    /// <summary>Stages the removal of an event key's override; false when there is none.</summary>
    Task<bool> RemoveAsync(string eventKey, CancellationToken cancellationToken = default);
}