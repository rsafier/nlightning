namespace NLightning.Domain.Client.Requests;

using Accounting.Books;
using Accounting.Books.Reports;
using Accounting.Enums;
using Accounting.Financial.Reports;
using Channels.ValueObjects;

/// <summary>
/// Asks for a report of the operational books (<c>ClientCommand.AccountingReport</c>, NL-602 A2). Every filter is
/// optional; each report reads the ones it understands.
/// </summary>
public sealed class AccountingReportClientRequest
{
    public AccountingReportKind Kind { get; init; } = AccountingReportKind.BalanceSheet;

    /// <summary>The start of the period (inclusive); unused by the balance sheet.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>The end of the period (exclusive); the time of the balance sheet.</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>Only this channel (channels view, register).</summary>
    public ChannelId? ChannelId { get; init; }

    /// <summary>The short channel id the channel filter came as (resolved through the loaded channels).</summary>
    public ShortChannelId? ChannelScid { get; init; }

    /// <summary>Only entries posting to this account (register): a role name (<c>Routing</c>) or an account name in
    /// effect (<c>income:lightning:routing</c>).</summary>
    public string? Account { get; init; }

    /// <summary>Only these event kinds (register).</summary>
    public IReadOnlyCollection<AccountingEventKind>? EventKinds { get; init; }

    /// <summary>The register's cursor: entries after this ledger sequence.</summary>
    public long AfterLedgerSeq { get; init; }

    /// <summary>The register's page size.</summary>
    public int Take { get; init; } = 100;

    /// <summary>The book (NL-602 A3-T6): the financial one for <c>--book financial</c>; the kinds from
    /// <see cref="AccountingReportKind.RealizedGains"/> on are the financial book's whatever this says.</summary>
    public AccountingBook Book { get; init; } = AccountingBook.Operational;

    /// <summary>The fiat currency of a financial report (null = the default, USD).</summary>
    public string? Currency { get; init; }

    /// <summary>The BTC price of the market values (balance sheet, lots, unrealized gains, risk capital).</summary>
    public decimal? Price { get; init; }

    /// <summary>The periods of the realized gains.</summary>
    public AccountingGainsGrouping Grouping { get; init; } = AccountingGainsGrouping.Month;

    /// <summary>The financial register's cursor adjustment (with <see cref="AfterLedgerSeq"/>), or null for none of
    /// that sequence's entries.</summary>
    public int? AfterAdjustment { get; init; }
}