namespace NLightning.Domain.Accounting.Financial.Reports;

using Books;
using Books.Reports;

/// <summary>
/// The sum of one account's postings in a report of the financial book (NL-602 A3-T6), in msat and in the report's
/// currency, in the report's sign convention (see the report).
/// </summary>
/// <param name="Account">The operational role the account's lines derive from.</param>
/// <param name="Name">The financial account's name.</param>
/// <param name="Category">Its category (the root of its name, <see cref="AccountingFiat.CategoryOf"/>).</param>
/// <param name="AmountMsat">The sum of the postings, msat.</param>
/// <param name="FiatAmount">The sum of the valued postings' fiat amounts in the report's currency (exact, not rounded).
/// </param>
/// <param name="UnvaluedPostings">How many postings with an msat amount have no value in that currency yet: while it is
/// not 0, <paramref name="FiatAmount"/> is partial.</param>
public sealed record AccountingFinancialAccountLine(
    AccountRole Account,
    string Name,
    AccountingAccountCategory Category,
    long AmountMsat,
    decimal FiatAmount,
    int UnvaluedPostings)
{
    /// <summary><see cref="AmountMsat"/> in satoshis, exact.</summary>
    public decimal AmountSat => AmountMsat / 1_000m;

    /// <summary>For an asset of a balance sheet asked at a price: <see cref="AmountMsat"/> at that price.</summary>
    public decimal? MarketValue { get; init; }
}

/// <summary>
/// The balances of the financial book at a time (NL-602 A3-T6, plan §6.2), in msat and in fiat: assets against
/// liabilities, equity and the earnings, so that <c>assets = liabilities + equity + earnings</c> in msat always and in
/// fiat once every posting is valued.
/// </summary>
/// <remarks>
/// <para>Assets and expenses are debit-positive; liabilities, equity and income credit-positive. The fiat amounts are
/// the book's values: each posting at its price when it happened (A3-T2) and the realized gains of disposals (A3-T4), so
/// an asset's fiat amount is what its sats are booked at, not what they fetch now; <see cref="Price"/> adds the market
/// value.</para>
/// <para>Closed periods (A3-T5) are not rolled into equity here: the earnings are the income less expenses of every
/// entry up to <see cref="At"/>.</para>
/// </remarks>
/// <param name="At">The time of the balances (entries that occurred before it), or null for every projected entry.</param>
/// <param name="ProjectedLedgerSeq">The financial book's cursor: the last operational entry it projected.</param>
/// <param name="Currency">The fiat currency of the amounts.</param>
/// <param name="Assets">Asset accounts.</param>
/// <param name="Liabilities">Liability accounts.</param>
/// <param name="Equity">Equity accounts.</param>
/// <param name="RetainedEarningsMsat">Income less expenses, msat.</param>
/// <param name="RetainedEarningsFiat">Income less expenses, fiat.</param>
public sealed record AccountingFinancialBalanceSheet(
    DateTimeOffset? At,
    long ProjectedLedgerSeq,
    string Currency,
    IReadOnlyList<AccountingFinancialAccountLine> Assets,
    IReadOnlyList<AccountingFinancialAccountLine> Liabilities,
    IReadOnlyList<AccountingFinancialAccountLine> Equity,
    long RetainedEarningsMsat,
    decimal RetainedEarningsFiat)
{
    /// <summary>The BTC price the market values were taken at (given or the latest stored), or null.</summary>
    public AccountingReportPrice? Price { get; init; }

    /// <summary>Postings of the book without a value in <see cref="Currency"/> (every account, income and expenses
    /// included).</summary>
    public int UnvaluedPostings { get; init; }

    public long TotalAssetsMsat => Assets.Sum(a => a.AmountMsat);
    public long TotalLiabilitiesMsat => Liabilities.Sum(a => a.AmountMsat);
    public long TotalEquityMsat => Equity.Sum(a => a.AmountMsat);
    public decimal TotalAssetsFiat => Assets.Sum(a => a.FiatAmount);
    public decimal TotalLiabilitiesFiat => Liabilities.Sum(a => a.FiatAmount);
    public decimal TotalEquityFiat => Equity.Sum(a => a.FiatAmount);

    /// <summary>The assets at <see cref="Price"/>, when one is set.</summary>
    public decimal? TotalAssetsMarketValue => Price is null ? null : Assets.Sum(a => a.MarketValue ?? 0m);

    /// <summary>Assets equal liabilities, equity and earnings in msat (always, unless the books are corrupt).</summary>
    public bool IsBalanced => TotalAssetsMsat == TotalLiabilitiesMsat + TotalEquityMsat + RetainedEarningsMsat;

    /// <summary>The same in fiat; meaningful once <see cref="UnvaluedPostings"/> is 0.</summary>
    public bool IsFiatBalanced => TotalAssetsFiat == TotalLiabilitiesFiat + TotalEquityFiat + RetainedEarningsFiat;
}

/// <summary>
/// Income and expenses of the financial book's entries that occurred in [<see cref="Since"/>, <see cref="Until"/>)
/// (NL-602 A3-T6), in msat and in fiat. Realized gains and losses are lines of their own accounts
/// (<c>income:gains:realized</c>, <c>expenses:losses:realized</c>); a gain has a fiat amount and no msat.
/// </summary>
/// <param name="Since">The start (inclusive), or null.</param>
/// <param name="Until">The end (exclusive), or null.</param>
/// <param name="ProjectedLedgerSeq">The financial book's cursor.</param>
/// <param name="Currency">The fiat currency.</param>
/// <param name="Income">Income accounts, credit-positive.</param>
/// <param name="Expenses">Expense accounts, debit-positive.</param>
public sealed record AccountingFinancialIncomeStatement(
    DateTimeOffset? Since,
    DateTimeOffset? Until,
    long ProjectedLedgerSeq,
    string Currency,
    IReadOnlyList<AccountingFinancialAccountLine> Income,
    IReadOnlyList<AccountingFinancialAccountLine> Expenses)
{
    public long TotalIncomeMsat => Income.Sum(a => a.AmountMsat);
    public long TotalExpensesMsat => Expenses.Sum(a => a.AmountMsat);
    public decimal TotalIncomeFiat => Income.Sum(a => a.FiatAmount);
    public decimal TotalExpensesFiat => Expenses.Sum(a => a.FiatAmount);
    public long NetIncomeMsat => TotalIncomeMsat - TotalExpensesMsat;
    public decimal NetIncomeFiat => TotalIncomeFiat - TotalExpensesFiat;

    /// <summary>Income and expense postings of the period without a value in <see cref="Currency"/>.</summary>
    public int UnvaluedPostings => Income.Sum(a => a.UnvaluedPostings) + Expenses.Sum(a => a.UnvaluedPostings);
}

/// <summary>A BTC price a report used.</summary>
/// <param name="PricePerBitcoin">The price of one BTC.</param>
/// <param name="Currency">Its currency.</param>
/// <param name="Time">The stored price's time, or null for a price the request gave.</param>
/// <param name="PriceId">The stored price's id, or null for a given one.</param>
public sealed record AccountingReportPrice(decimal PricePerBitcoin, string Currency, DateTimeOffset? Time, long? PriceId)
{
    /// <summary>Whether the request gave the price (else it is the latest stored one).</summary>
    public bool IsGiven => PriceId is null;
}