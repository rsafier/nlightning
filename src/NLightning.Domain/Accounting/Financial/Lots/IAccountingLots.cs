namespace NLightning.Domain.Accounting.Financial.Lots;

/// <summary>
/// What a lot import did (<c>nltg accounting lots import</c>, D-A9; NL-602 A3-T4).
/// </summary>
/// <param name="Currency">The lots' currency (the book's).</param>
/// <param name="Imported">How many lots were stored.</param>
/// <param name="ImportedMsat">Their msat (the opening balances' total).</param>
/// <param name="ImportedCost">Their total cost.</param>
/// <param name="OpeningMsat">The opening balances' total in the books.</param>
/// <param name="ReplacedLots">How many lots of an earlier import were replaced.</param>
/// <param name="ProjectedEntries">How many financial entries the rebuild that followed projected.</param>
public sealed record AccountingLotImportResult(
    string Currency,
    int Imported,
    long ImportedMsat,
    decimal ImportedCost,
    long OpeningMsat,
    int ReplacedLots,
    int ProjectedEntries)
{
    /// <summary>The msat added to (or, negative, taken from) the last lot so the lots hold the opening balances to the
    /// msat (less than one sat).</summary>
    public long AdjustedMsat { get; init; }
}

/// <summary>
/// The lot import of the financial book (D-A9; NL-602 A3-T4, IPC 45 <c>lots import</c>): before the first period close
/// the operator may replace the opening balances' lots (valued at the cutover price, <c>BasisEstimated</c>) with the
/// real ones.
/// </summary>
public interface IAccountingLots
{
    /// <summary>
    /// Replaces the opening balances' lots with <paramref name="lots"/> (and an earlier import's), then rebuilds the
    /// financial book from the start so every disposal relieves the imported lots.
    /// </summary>
    /// <param name="currency">The lots' currency, or null for the book's; it must be the book's.</param>
    /// <param name="lots">The lots; their msat must add up to the opening balances' within one sat.</param>
    /// <param name="cancellationToken">Cancels before the import commits.</param>
    /// <exception cref="InvalidOperationException">The financial book is off, a period is closed already (after the
    /// first close the basis changes only through an adjustment), or the books hold no opening balance.</exception>
    /// <exception cref="ArgumentException">A bad lot, another currency, or totals that differ.</exception>
    Task<AccountingLotImportResult> ImportAsync(string? currency, IReadOnlyList<AccountingLotPoint> lots,
                                                CancellationToken cancellationToken = default);
}