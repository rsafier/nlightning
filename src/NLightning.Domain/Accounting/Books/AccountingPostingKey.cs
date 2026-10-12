namespace NLightning.Domain.Accounting.Books;

/// <summary>The key of one posting: its entry (book, ledger sequence, adjustment) and its line index.</summary>
public readonly record struct AccountingPostingKey(AccountingBook Book, long LedgerSeq, int Adjustment, int Index);