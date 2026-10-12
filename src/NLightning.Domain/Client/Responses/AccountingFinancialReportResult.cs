namespace NLightning.Domain.Client.Responses;

using Accounting.Financial.Reports;

/// <summary>
/// A report of the financial book (NL-602 A3-T6; <c>accounting report ... --book financial</c>): the one of the kind
/// asked for is set.
/// </summary>
/// <param name="Currency">The fiat currency of the amounts.</param>
public sealed record AccountingFinancialReportResult(string Currency)
{
    public AccountingFinancialBalanceSheet? BalanceSheet { get; init; }
    public AccountingFinancialIncomeStatement? IncomeStatement { get; init; }
    public AccountingRealizedGainsReport? RealizedGains { get; init; }

    /// <summary>The open lots (kinds lots and unrealized gains).</summary>
    public AccountingLotsReport? Lots { get; init; }

    /// <summary>The financial register (kinds register and unclassified).</summary>
    public AccountingFinancialRegister? Register { get; init; }

    public AccountingUnvaluedReport? Unvalued { get; init; }
    public AccountingRiskCapitalReport? RiskCapital { get; init; }
}