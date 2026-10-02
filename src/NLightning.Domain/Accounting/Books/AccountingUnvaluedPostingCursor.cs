namespace NLightning.Domain.Accounting.Books;

/// <summary>
/// A position in the back-valuation's work list (A3-T2): postings are ordered by time, ledger sequence, adjustment and
/// line; a page continues strictly after the cursor.
/// </summary>
public readonly record struct AccountingUnvaluedPostingCursor(
    DateTimeOffset OccurredAt,
    long LedgerSeq,
    int Adjustment,
    int Index)
{
    /// <summary>The cursor of a listed posting: the next page starts after it.</summary>
    public static AccountingUnvaluedPostingCursor After(AccountingUnvaluedPosting posting)
    {
        ArgumentNullException.ThrowIfNull(posting);
        return new AccountingUnvaluedPostingCursor(posting.OccurredAt, posting.Key.LedgerSeq, posting.Key.Adjustment,
                                                   posting.Key.Index);
    }

    /// <summary>A cursor before every posting at or after <paramref name="time"/> (ledger sequences start at 1).</summary>
    public static AccountingUnvaluedPostingCursor StartOf(DateTimeOffset time) => new(time, 0, 0, 0);
}