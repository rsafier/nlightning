namespace NLightning.Domain.Accounting.Books;

/// <summary>One line of an entry: a debit (positive) or credit (negative) to an account, in msat.</summary>
public sealed record AccountingPosting(AccountRole Account, long AmountMsat);
