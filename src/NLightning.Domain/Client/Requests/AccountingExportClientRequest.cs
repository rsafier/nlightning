namespace NLightning.Domain.Client.Requests;

using Accounting.Books.Export;

/// <summary>
/// Asks for one page of an export of the books (<c>ClientCommand.AccountingExport</c>, NL-602 A2). The client asks
/// from cursor 0 (the page with the format's header) and then with each answer's <c>NextAfter</c> while it has more.
/// </summary>
public sealed class AccountingExportClientRequest
{
    public AccountingExportFormat Format { get; init; } = AccountingExportFormat.Hledger;

    /// <summary>The start of the period (inclusive).</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>The end of the period (exclusive).</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>Entries after this ledger sequence (0 starts the export).</summary>
    public long AfterLedgerSeq { get; init; }

    /// <summary>The page size in entries.</summary>
    public int Take { get; init; } = 1_000;
}