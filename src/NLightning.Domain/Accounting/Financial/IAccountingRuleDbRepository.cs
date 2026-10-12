namespace NLightning.Domain.Accounting.Financial;

/// <summary>
/// The classification rules (<c>AccountingRules</c>, A3-T3, D-A10). Writes are staged and committed by the unit of
/// work's save.
/// </summary>
public interface IAccountingRuleDbRepository
{
    /// <summary>Stages a new rule (its <see cref="AccountingRule.Id"/> is ignored and assigned by the save).</summary>
    void Add(AccountingRule rule);

    /// <summary>The rule with this id, or null.</summary>
    Task<AccountingRule?> GetByIdAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>The saved rules in match order (priority, then id); only the enabled ones when
    /// <paramref name="enabledOnly"/>.</summary>
    Task<IReadOnlyList<AccountingRule>> ListAsync(bool enabledOnly, CancellationToken cancellationToken = default);

    /// <summary>Stages the rule's enabled flag; false when there is no such rule.</summary>
    Task<bool> SetEnabledAsync(long id, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Stages the removal of a rule; false when there is no such rule.</summary>
    Task<bool> RemoveAsync(long id, CancellationToken cancellationToken = default);
}