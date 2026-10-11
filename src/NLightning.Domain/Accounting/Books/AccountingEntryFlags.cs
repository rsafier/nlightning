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
    Unvalued = 1 << 2,

    /// <summary>
    /// The financial projector (A3-T4) projected the entry before a price of its time was usable, so its lines are
    /// unvalued and it relieved or opened lots without a fiat value. Once the back-valuation has valued its lines, the
    /// projector projects it again, with every open entry after it, at that price (lots, reliefs and the realized gain
    /// included).
    /// </summary>
    PendingValuation = 1 << 3,

    /// <summary>
    /// The entry disposed of lots of which one has no fiat cost (A3-T4): its realized gain is pending valuation (never
    /// zero) until the lot's acquisition is valued; the entry carries no gain line meanwhile.
    /// </summary>
    GainPending = 1 << 4,

    /// <summary>
    /// The adjustment projects an operational entry dated in a closed period (A3-T5's <c>LateFact</c>, NL-671): it is a
    /// projection of the feed like an entry of the open period, so a rollback or a rebuild of the open period deletes
    /// it (with its lots and reliefs) and the replay stages it again, valued at the price of the fact's own time. With
    /// <see cref="PendingValuation"/> its lines wait for a price usable at the fact's time; the back-valuation never
    /// values them in place (it would use the adjustment's date) but lowers the financial cursor to the fact.
    /// </summary>
    LateFact = 1 << 5
}