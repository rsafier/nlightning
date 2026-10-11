namespace NLightning.Domain.Accounting.Books;

/// <summary>
/// What <see cref="IAccountingBooksDbRepository.ResetToCloseAsync"/> resets a book to (A3-T5, D-A8): the state at the
/// last close.
/// </summary>
/// <param name="CursorLedgerSeq">The book's cursor afterwards (the close's replay point, 0 before any close).</param>
/// <param name="ClosingBalances">The running balances of the closed periods (empty before any close); the kept
/// adjustments of the open period are added to them.</param>
public sealed record AccountingBookReset(long CursorLedgerSeq, IReadOnlyList<AccountingAccountBalance> ClosingBalances);