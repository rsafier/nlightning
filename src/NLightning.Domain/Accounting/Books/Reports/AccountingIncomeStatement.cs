namespace NLightning.Domain.Accounting.Books.Reports;

/// <summary>
/// Income and expenses of the entries that occurred in [<see cref="Since"/>, <see cref="Until"/>) (plan §6.1).
/// </summary>
/// <param name="Since">The start (inclusive), or null for the start of the books.</param>
/// <param name="Until">The end (exclusive), or null for now.</param>
/// <param name="ProjectedLedgerSeq">The books' cursor: the last event the books hold.</param>
/// <param name="Income">The income accounts with postings, credit-positive (what we earned).</param>
/// <param name="Expenses">The expense accounts with postings, debit-positive (what we spent).</param>
public sealed record AccountingIncomeStatement(
    DateTimeOffset? Since,
    DateTimeOffset? Until,
    long ProjectedLedgerSeq,
    IReadOnlyList<AccountingAccountLine> Income,
    IReadOnlyList<AccountingAccountLine> Expenses)
{
    public long TotalIncomeMsat => Income.Sum(a => a.AmountMsat);
    public long TotalExpensesMsat => Expenses.Sum(a => a.AmountMsat);

    /// <summary>Income less expenses.</summary>
    public long NetIncomeMsat => TotalIncomeMsat - TotalExpensesMsat;
}