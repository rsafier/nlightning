namespace NLightning.Domain.Client.Responses;

using Accounting.Books;
using Accounting.Books.Reports;

/// <summary>
/// The answer to <c>accounting report</c> (<c>ClientCommand.AccountingReport</c>, NL-602 A2): the report of the kind
/// asked for (the channels view answers both <see cref="AccountingReportKind.Channels"/> and
/// <see cref="AccountingReportKind.Peers"/>).
/// </summary>
/// <param name="Kind">The report.</param>
/// <param name="Names">The account names in effect (for the register's postings).</param>
public sealed record AccountingReportClientResponse(AccountingReportKind Kind, AccountNames Names)
{
    public AccountingBalanceSheet? BalanceSheet { get; init; }
    public AccountingIncomeStatement? IncomeStatement { get; init; }
    public AccountingChannelsReport? Channels { get; init; }
    public AccountingFeesReport? Fees { get; init; }
    public AccountingRegister? Register { get; init; }
}