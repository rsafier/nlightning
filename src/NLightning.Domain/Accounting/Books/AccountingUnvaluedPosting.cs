namespace NLightning.Domain.Accounting.Books;

/// <summary>A posting without a fiat value (A3-T2's work list): where it is, when its entry occurred and what it holds.</summary>
public sealed record AccountingUnvaluedPosting(
    AccountingPostingKey Key,
    DateTimeOffset OccurredAt,
    AccountRole Account,
    string? AccountName,
    long AmountMsat,
    string? ClosedPeriodId)
{
    /// <summary>The flags of the posting's entry (A3-T4: the back-valuation lowers the financial cursor for the entries
    /// the projector marked <see cref="AccountingEntryFlags.PendingValuation"/>).</summary>
    public AccountingEntryFlags EntryFlags { get; init; }
}