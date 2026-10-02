// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Accounting;

/// <summary>
/// A manual reclassification (<c>AccountingOverride</c>, NL-602 A3-T3, plan §6.2; migration
/// <c>AddAccountingFinancial</c>): the financial book sends the entry of <see cref="EventKey"/> to
/// <see cref="Account"/>, ahead of every rule. The only books state that cannot be rebuilt from the feed. Unique per
/// <see cref="EventKey"/> (<c>classify set</c> replaces the row).
/// </summary>
public class AccountingOverrideEntity
{
    /// <summary>Storage id (identity).</summary>
    public long Id { get; set; }

    public required string EventKey { get; set; }

    /// <summary>The financial account name.</summary>
    public required string Account { get; set; }

    public string? Note { get; set; }

    /// <summary>When it was set or last replaced (UTC ticks).</summary>
    public required DateTimeOffset CreatedAt { get; set; }

    // Default constructor for EF Core
    internal AccountingOverrideEntity()
    {
    }
}