namespace NLightning.Domain.Accounting.Books.Reports;

/// <summary>
/// A page of the books' entries with their postings, in ledger order (plan §6.1, the plain register).
/// </summary>
/// <param name="Entries">The page.</param>
/// <param name="NextAfter">The cursor of the next page: the last entry's ledger sequence, or the request's cursor when
/// the page is empty.</param>
/// <param name="HasMore">Whether the page is full (more entries may follow).</param>
/// <param name="ProjectedLedgerSeq">The books' cursor.</param>
public sealed record AccountingRegister(
    IReadOnlyList<AccountingEntry> Entries,
    long NextAfter,
    bool HasMore,
    long ProjectedLedgerSeq);