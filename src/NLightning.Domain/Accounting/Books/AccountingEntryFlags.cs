namespace NLightning.Domain.Accounting.Books;

/// <summary>
/// Flags of a books entry (column <c>AccountingEntries.Flags</c>, migration <c>AddAccountingFinancial</c>). The values
/// are persisted: never renumber them; the A3 lanes add theirs after the last one.
/// </summary>
[Flags]
public enum AccountingEntryFlags
{
    None = 0,

    /// <summary>
    /// The entry corrects a fact of a closed period and is dated in the open period (D-A8): a late write of a
    /// projector, an override, a rule change or a price filled in after the close.
    /// </summary>
    Adjustment = 1 << 0,

    /// <summary>A line of the entry went to an <c>*:unclassified</c> account (A3-T3), listed for review.</summary>
    Unclassified = 1 << 1,

    /// <summary>A line of the entry has no fiat value yet (A3-T2): the back-valuation job fills it.</summary>
    Unvalued = 1 << 2
}