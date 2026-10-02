namespace NLightning.Domain.Accounting.Books;

/// <summary>A posting without a fiat value (A3-T2's work list): where it is, when its entry occurred and what it holds.</summary>
public sealed record AccountingUnvaluedPosting(
    AccountingPostingKey Key,
    DateTimeOffset OccurredAt,
    AccountRole Account,
    string? AccountName,
    long AmountMsat,
    string? ClosedPeriodId);