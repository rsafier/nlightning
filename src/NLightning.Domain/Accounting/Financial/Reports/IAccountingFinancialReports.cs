namespace NLightning.Domain.Accounting.Financial.Reports;

using Books;

/// <summary>
/// The reports of the financial book (plan <c>docs/agents/ACCOUNTING_PLAN.md</c> §6.2, §9 A3-T6; IPC 43 with
/// <c>--book financial</c>): balance sheet and income statement in msat and fiat, realized gains by period, the open
/// lots and their unrealized gains at a price, the rows waiting for a value or a classification, and the risk-weighted
/// capital of a live snapshot.
/// </summary>
/// <remarks>
/// Every book report refuses when the books are off (<see cref="Books.Reports.AccountingBooksDisabledException"/>) or the
/// financial book is (<see cref="AccountingFinancialBooksDisabledException"/>), then seals, projects the operational book
/// and the financial one (<see cref="IFinancialBooksProjector"/>), then reads. A <c>currency</c> argument is an
/// ISO 4217 code (null = <see cref="AccountingFiat.DefaultCurrency"/>); a bad one throws
/// <see cref="ArgumentException"/>, as does an empty period.
/// </remarks>
public interface IAccountingFinancialReports
{
    /// <summary>The balances at <paramref name="at"/> (null = now); with <paramref name="price"/> (or, when
    /// <paramref name="withMarketValue"/>, the latest stored price) the assets' market value too.</summary>
    Task<AccountingFinancialBalanceSheet> GetBalanceSheetAsync(DateTimeOffset? at, string? currency, decimal? price,
                                                               bool withMarketValue = false,
                                                               CancellationToken cancellationToken = default);

    /// <summary>Income and expenses in [<paramref name="since"/>, <paramref name="until"/>).</summary>
    Task<AccountingFinancialIncomeStatement> GetIncomeStatementAsync(DateTimeOffset? since, DateTimeOffset? until,
                                                                     string? currency,
                                                                     CancellationToken cancellationToken = default);

    /// <summary>The realized gains of the reliefs made in [<paramref name="since"/>, <paramref name="until"/>), by
    /// period.</summary>
    Task<AccountingRealizedGainsReport> GetRealizedGainsAsync(DateTimeOffset? since, DateTimeOffset? until,
                                                              AccountingGainsGrouping grouping, string? currency,
                                                              CancellationToken cancellationToken = default);

    /// <summary>
    /// A page of the open lots after lot <paramref name="afterLotId"/>. With <paramref name="price"/> or, when
    /// <paramref name="requirePrice"/>, the latest stored price, their market value and unrealized gain;
    /// <paramref name="requirePrice"/> without either throws <see cref="InvalidOperationException"/>.
    /// </summary>
    Task<AccountingLotsReport> GetLotsAsync(long afterLotId, int take, string? currency, decimal? price,
                                            bool requirePrice, CancellationToken cancellationToken = default);

    /// <summary>A page of the financial book's entries (<paramref name="query"/>'s book is ignored: always the financial
    /// one; <see cref="AccountingEntryQuery.WithFlags"/> lists the unclassified or adjustment entries).</summary>
    Task<AccountingFinancialRegister> GetRegisterAsync(AccountingEntryQuery query,
                                                       CancellationToken cancellationToken = default);

    /// <summary>The oldest <paramref name="take"/> postings of the financial book without a fiat value.</summary>
    Task<AccountingUnvaluedReport> GetUnvaluedAsync(int take, CancellationToken cancellationToken = default);

    /// <summary>
    /// The risk-weighted capital of a live snapshot (books on or off: it reads the node, not the books), in fiat at
    /// <paramref name="price"/> or the latest stored price when there is one.
    /// </summary>
    Task<AccountingRiskCapitalReport> GetRiskCapitalAsync(string? currency, decimal? price,
                                                          CancellationToken cancellationToken = default);
}

/// <summary>
/// The financial book is off (<c>Accounting:Profile=Operational</c>, D-A7): there is nothing to report from it.
/// </summary>
public sealed class AccountingFinancialBooksDisabledException : InvalidOperationException
{
    public AccountingFinancialBooksDisabledException()
        : base("The financial books are off (Accounting:Profile=Operational); the operational reports still work "
             + "without --book financial.")
    {
    }
}