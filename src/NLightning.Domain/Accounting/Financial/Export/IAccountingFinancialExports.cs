namespace NLightning.Domain.Accounting.Financial.Export;

using Books.Export;

/// <summary>
/// A page of an export of the financial book (NL-602 A3-T6, IPC 44 with <c>--book financial</c>): the entries after
/// (<see cref="AfterLedgerSeq"/>, <see cref="AfterAdjustment"/>) that occurred in [<see cref="Since"/>,
/// <see cref="Until"/>), in (ledger sequence, adjustment) order.
/// </summary>
/// <param name="Format">The format.</param>
/// <param name="AfterLedgerSeq">The cursor's ledger sequence; 0 (with no <see cref="AfterAdjustment"/>) starts the
/// export, and its page carries the header.</param>
/// <param name="Take">At most this many entries.</param>
/// <param name="Since">The start (inclusive), or null.</param>
/// <param name="Until">The end (exclusive), or null.</param>
/// <param name="Currency">The fiat currency of the costs and prices (null = <c>AccountingFiat.DefaultCurrency</c>).
/// </param>
/// <param name="AfterAdjustment">The cursor's adjustment: entries of <see cref="AfterLedgerSeq"/> with a higher
/// adjustment come next; null = none of them (the start of the next sequence).</param>
public sealed record AccountingFinancialExportQuery(
    AccountingExportFormat Format,
    long AfterLedgerSeq,
    int Take,
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null,
    string? Currency = null,
    int? AfterAdjustment = null)
{
    /// <summary>Whether this is the first page (the one with the header).</summary>
    public bool IsFirstPage => AfterLedgerSeq == 0 && AfterAdjustment is null;
}

/// <summary>
/// One page of an export of the financial book; concatenated in order, the pages are the whole document.
/// </summary>
/// <param name="Text">The page's text.</param>
/// <param name="NextAfter">The next cursor's ledger sequence.</param>
/// <param name="NextAfterAdjustment">The next cursor's adjustment (the last entry's), or null when the page is empty.
/// </param>
/// <param name="HasMore">Whether more entries may follow.</param>
/// <param name="EntryCount">How many entries the page read.</param>
public sealed record AccountingFinancialExportChunk(
    string Text,
    long NextAfter,
    int? NextAfterAdjustment,
    bool HasMore,
    int EntryCount);

/// <summary>
/// Exports of the financial book (plan §6.2, §11; streamed to the client page by page, the daemon never writes a file):
/// hledger with <c>@@</c> costs and <c>P</c> price directives, beancount with <c>{{ }}</c> costs and <c>price</c>
/// directives, CSV with fiat columns.
/// </summary>
/// <remarks>Throws <c>AccountingBooksDisabledException</c> or <c>AccountingFinancialBooksDisabledException</c> when
/// the books or the financial book are off.</remarks>
public interface IAccountingFinancialExports
{
    /// <summary>One page of the export (projects what is sealed first).</summary>
    Task<AccountingFinancialExportChunk> ExportAsync(AccountingFinancialExportQuery query,
                                                     CancellationToken cancellationToken = default);
}