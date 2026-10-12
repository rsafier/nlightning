namespace NLightning.Domain.Accounting.Books.Export;

/// <summary>
/// The formats of an export of the books (plan §6.1, IPC 44). The values go on the wire: never renumber them.
/// </summary>
public enum AccountingExportFormat
{
    /// <summary>An hledger journal, amounts in the exact commodity <c>msat</c>.</summary>
    Hledger = 1,

    /// <summary>A beancount ledger, amounts in the currency <c>MSAT</c>.</summary>
    Beancount = 2,

    /// <summary>CSV, one row per posting.</summary>
    Csv = 3
}

/// <summary>
/// A page of an export: the entries after <see cref="AfterLedgerSeq"/> that occurred in [<see cref="Since"/>,
/// <see cref="Until"/>).
/// </summary>
/// <param name="Format">The format.</param>
/// <param name="AfterLedgerSeq">Only entries after this ledger sequence; 0 starts the export (the format's header is
/// written with the first page).</param>
/// <param name="Take">At most this many entries.</param>
/// <param name="Since">The start (inclusive), or null.</param>
/// <param name="Until">The end (exclusive), or null.</param>
public sealed record AccountingExportQuery(
    AccountingExportFormat Format,
    long AfterLedgerSeq,
    int Take,
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null);

/// <summary>
/// One page of an export's text. The pages of an export, concatenated in order, are the whole document.
/// </summary>
/// <param name="Text">The text of the page.</param>
/// <param name="NextAfter">The cursor of the next page.</param>
/// <param name="HasMore">Whether more entries may follow.</param>
/// <param name="EntryCount">How many entries the page read (entries without postings included, though the journal
/// formats leave them out).</param>
public sealed record AccountingExportChunk(string Text, long NextAfter, bool HasMore, int EntryCount);

/// <summary>
/// Exports of the books (plan §6.1, §11: streamed to the client page by page; the daemon never writes a file).
/// </summary>
/// <remarks>Throws <see cref="Reports.AccountingBooksDisabledException"/> when the books are off.</remarks>
public interface IAccountingExports
{
    /// <summary>One page of the export (projects what is sealed first).</summary>
    Task<AccountingExportChunk> ExportAsync(AccountingExportQuery query, CancellationToken cancellationToken = default);
}