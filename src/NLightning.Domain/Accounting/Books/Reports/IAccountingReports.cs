namespace NLightning.Domain.Accounting.Books.Reports;

using Channels.ValueObjects;

/// <summary>
/// The reports of the operational books (plan <c>docs/agents/ACCOUNTING_PLAN.md</c> §6.1 "Reports", IPC 43). Every
/// report first projects what is sealed (<see cref="IAccountingBooks.ProjectNowAsync"/>), so it includes the events
/// committed a moment ago.
/// </summary>
/// <remarks>Every member throws <see cref="AccountingBooksDisabledException"/> when the books are off.</remarks>
public interface IAccountingReports
{
    /// <summary>The balances at <paramref name="at"/> (null = now).</summary>
    Task<AccountingBalanceSheet> GetBalanceSheetAsync(DateTimeOffset? at, CancellationToken cancellationToken = default);

    /// <summary>Income and expenses in [<paramref name="since"/>, <paramref name="until"/>).</summary>
    Task<AccountingIncomeStatement> GetIncomeStatementAsync(DateTimeOffset? since, DateTimeOffset? until,
                                                            CancellationToken cancellationToken = default);

    /// <summary>The per-channel and per-peer view of [<paramref name="since"/>, <paramref name="until"/>), of one
    /// channel when <paramref name="channelId"/> is set.</summary>
    Task<AccountingChannelsReport> GetChannelsReportAsync(DateTimeOffset? since, DateTimeOffset? until,
                                                          ChannelId? channelId = null,
                                                          CancellationToken cancellationToken = default);

    /// <summary>The fee breakdown of [<paramref name="since"/>, <paramref name="until"/>).</summary>
    Task<AccountingFeesReport> GetFeesReportAsync(DateTimeOffset? since, DateTimeOffset? until,
                                                  CancellationToken cancellationToken = default);

    /// <summary>A page of the register.</summary>
    Task<AccountingRegister> GetRegisterAsync(AccountingEntryQuery query, CancellationToken cancellationToken = default);
}