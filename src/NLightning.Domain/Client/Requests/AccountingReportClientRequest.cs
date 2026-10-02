namespace NLightning.Domain.Client.Requests;

using Accounting.Books.Reports;
using Accounting.Enums;
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
}