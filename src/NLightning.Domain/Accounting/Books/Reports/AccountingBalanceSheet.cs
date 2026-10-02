namespace NLightning.Domain.Accounting.Books.Reports;

/// <summary>
/// The account balances at a time (plan §6.1 "Reports"): assets against liabilities, equity and the earnings of the
/// current period, so that <c>assets = liabilities + equity + earnings</c>.
/// </summary>
/// <remarks>
/// Assets are debit-positive (what we hold); liabilities and equity are credit-positive (deposits in
/// <c>equity:transfers:in</c> show positive, withdrawals in <c>equity:transfers:out</c> negative). There is no period
/// close yet (plan A3), so the current period is the whole history up to <see cref="At"/> and the earnings are its net
/// income (income less expenses).
/// </remarks>
/// <param name="At">The time of the balances (entries that occurred before it), or null for now (every projected
/// entry).</param>
/// <param name="ProjectedLedgerSeq">The books' cursor: the last event the books hold.</param>
/// <param name="Assets">The asset accounts with postings.</param>
/// <param name="Liabilities">The liability accounts with postings (none in the operational chart).</param>
/// <param name="Equity">The equity accounts with postings.</param>
/// <param name="RetainedEarningsMsat">The current period's net income (income less expenses).</param>
public sealed record AccountingBalanceSheet(
    DateTimeOffset? At,
    long ProjectedLedgerSeq,
    IReadOnlyList<AccountingAccountLine> Assets,
    IReadOnlyList<AccountingAccountLine> Liabilities,
    IReadOnlyList<AccountingAccountLine> Equity,
    long RetainedEarningsMsat)
{
    public long TotalAssetsMsat => Assets.Sum(a => a.AmountMsat);
    public long TotalLiabilitiesMsat => Liabilities.Sum(a => a.AmountMsat);
    public long TotalEquityMsat => Equity.Sum(a => a.AmountMsat);

    /// <summary>Whether assets equal liabilities, equity and earnings (always, unless the books are corrupt).</summary>
    public bool IsBalanced => TotalAssetsMsat == TotalLiabilitiesMsat + TotalEquityMsat + RetainedEarningsMsat;
}