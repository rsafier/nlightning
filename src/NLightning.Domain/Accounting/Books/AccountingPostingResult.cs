namespace NLightning.Domain.Accounting.Books;

/// <summary>
/// What the posting rules make of one event (<see cref="AccountingPostingRules.Evaluate"/>): the postings of its entry
/// (they sum to zero; none for an event that moves no money) and, when the rules had to fall back or found something
/// the reader should know, a note for <see cref="AccountingEntry.Note"/>.
/// </summary>
public sealed record AccountingPostingResult(IReadOnlyList<AccountingPosting> Postings, string? Note = null);