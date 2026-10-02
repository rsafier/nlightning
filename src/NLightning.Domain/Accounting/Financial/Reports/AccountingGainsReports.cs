namespace NLightning.Domain.Accounting.Financial.Reports;

using Books;

/// <summary>How the realized gains are grouped (IPC 43 key). The values go on the wire: never renumber them.</summary>
public enum AccountingGainsGrouping
{
    Month = 1,
    Quarter = 2,
    Year = 3,

    /// <summary>One line for the whole window.</summary>
    Total = 4
}

/// <summary>
/// The gains realized by disposals in [<see cref="Since"/>, <see cref="Until"/>) (NL-602 A3-T6, D-A12), from the lot
/// reliefs: for each period, what was disposed of, its cost basis and the proceeds. A relief whose cost or proceeds is
/// not valued yet is counted as pending valuation, never as a zero gain.
/// </summary>
/// <param name="Since">The start (inclusive), or null.</param>
/// <param name="Until">The end (exclusive), or null.</param>
/// <param name="Currency">The fiat currency.</param>
/// <param name="Grouping">The periods.</param>
/// <param name="Periods">One line per period with a relief, oldest first (UTC calendar periods).</param>
/// <param name="Total">The window's sum.</param>
public sealed record AccountingRealizedGainsReport(
    DateTimeOffset? Since,
    DateTimeOffset? Until,
    string Currency,
    AccountingGainsGrouping Grouping,
    IReadOnlyList<AccountingRealizedGainsLine> Periods,
    AccountingRealizedGainsLine Total);

/// <summary>
/// The realized gains of one period. Amounts are exact; a gain is proceeds less cost basis of the valued reliefs.
/// </summary>
/// <param name="Period">The period's name (<c>2026-09</c>, <c>2026-Q3</c>, <c>2026</c>, or <c>total</c>).</param>
/// <param name="Start">Its first instant (null for an open-ended total).</param>
/// <param name="End">The first instant after it (null for an open-ended total).</param>
public sealed record AccountingRealizedGainsLine(string Period, DateTimeOffset? Start, DateTimeOffset? End)
{
    /// <summary>How many reliefs (lot parts disposed of).</summary>
    public int Reliefs { get; init; }

    /// <summary>Every msat disposed of, valued or not.</summary>
    public long DisposedMsat { get; init; }

    /// <summary>The cost basis of the valued reliefs.</summary>
    public decimal CostBasis { get; init; }

    /// <summary>The proceeds of the valued reliefs (0 for a loss with nothing received).</summary>
    public decimal Proceeds { get; init; }

    /// <summary>Proceeds less cost basis of the valued reliefs.</summary>
    public decimal Gain => Proceeds - CostBasis;

    /// <summary>The part of <see cref="Gain"/> from lots held at most a year (365 days).</summary>
    public decimal ShortTermGain { get; init; }

    /// <summary>The part of <see cref="Gain"/> from lots held longer than a year.</summary>
    public decimal LongTermGain { get; init; }

    /// <summary>Reliefs whose cost or proceeds has no value in the report's currency yet.</summary>
    public int PendingValuation { get; init; }

    /// <summary>The msat of those reliefs.</summary>
    public long PendingValuationMsat { get; init; }

    /// <summary>The msat disposed of from lots whose basis is estimated (the cutover's opening lots, D-A9).</summary>
    public long BasisEstimatedMsat { get; init; }
}

/// <summary>
/// The open cost-basis lots (NL-602 A3-T6): a page of them, and the totals over all of them. With a price, each lot's
/// market value and unrealized gain (market value less the cost basis of what is left).
/// </summary>
/// <param name="Currency">The fiat currency.</param>
/// <param name="Price">The price of the market values (given, or the latest stored), or null.</param>
/// <param name="Lots">The page, oldest first (acquisition time, then id).</param>
/// <param name="NextAfterLotId">The cursor of the next page (the last lot's id, or the request's cursor).</param>
/// <param name="HasMore">Whether more lots follow.</param>
/// <param name="Totals">The sums over every open lot, not only the page.</param>
public sealed record AccountingLotsReport(
    string Currency,
    AccountingReportPrice? Price,
    IReadOnlyList<AccountingLotLine> Lots,
    long NextAfterLotId,
    bool HasMore,
    AccountingLotTotals Totals);

/// <summary>One open lot.</summary>
/// <param name="Lot">The lot as stored.</param>
/// <param name="RemainingCostBasis">The cost of what is left (<c>cost x remaining / original</c>), or null while the
/// lot has no cost in the report's currency.</param>
/// <param name="MarketValue">What is left at the report's price, or null without a price.</param>
public sealed record AccountingLotLine(AccountingLot Lot, decimal? RemainingCostBasis, decimal? MarketValue)
{
    /// <summary>Market value less cost basis, when both are known.</summary>
    public decimal? UnrealizedGain => MarketValue - RemainingCostBasis;
}

/// <summary>The sums over every open lot.</summary>
public sealed record AccountingLotTotals
{
    public int OpenLots { get; init; }
    public long RemainingMsat { get; init; }

    /// <summary>The cost basis of the lots with a cost in the report's currency.</summary>
    public decimal CostBasis { get; init; }

    /// <summary>The msat of the lots without a cost in the report's currency (pending valuation).</summary>
    public long UnvaluedMsat { get; init; }

    public int UnvaluedLots { get; init; }

    /// <summary>The msat of the lots whose basis is estimated (D-A9).</summary>
    public long BasisEstimatedMsat { get; init; }

    /// <summary>Every open lot at the report's price, or null without a price.</summary>
    public decimal? MarketValue { get; init; }

    /// <summary>The market value of the valued lots less their cost basis, or null without a price.</summary>
    public decimal? UnrealizedGain { get; init; }
}

/// <summary>
/// A page of the financial book's entries in (ledger sequence, adjustment) order (NL-602 A3-T6): the register of the
/// financial book, or the entries flagged for review (<see cref="AccountingEntryFlags.Unclassified"/>).
/// </summary>
/// <param name="Entries">The page.</param>
/// <param name="NextAfter">The ledger sequence of the next page's cursor.</param>
/// <param name="NextAfterAdjustment">The adjustment of the next page's cursor (pass both).</param>
/// <param name="HasMore">Whether the page is full.</param>
/// <param name="ProjectedLedgerSeq">The financial book's cursor.</param>
public sealed record AccountingFinancialRegister(
    IReadOnlyList<AccountingEntry> Entries,
    long NextAfter,
    int NextAfterAdjustment,
    bool HasMore,
    long ProjectedLedgerSeq);

/// <summary>
/// The oldest postings of the financial book without a fiat value (NL-602 A3-T6): what the back-valuation (A3-T2) still
/// has to fill, and what blocks a period close (A3-T5).
/// </summary>
/// <param name="Postings">At most the page size, oldest first.</param>
/// <param name="HasMore">Whether there are more.</param>
/// <param name="ProjectedLedgerSeq">The financial book's cursor.</param>
public sealed record AccountingUnvaluedReport(
    IReadOnlyList<AccountingUnvaluedPosting> Postings,
    bool HasMore,
    long ProjectedLedgerSeq);