namespace NLightning.Domain.Accounting.Financial.Classification;

using Books;
using Enums;

/// <summary>
/// One event classified by <c>classify rule test</c> (NL-602 A3-T3).
/// </summary>
/// <param name="LedgerSeq">The event's ledger sequence.</param>
/// <param name="EventKey">The event key.</param>
/// <param name="Kind">The event's kind.</param>
/// <param name="OccurredAt">When it happened.</param>
/// <param name="Classification">The engine's answer with the stored rules and override.</param>
/// <param name="Lines">The financial lines that answer gives (role, msat and account name).</param>
public sealed record AccountingClassifyTestResult(
    long LedgerSeq,
    string EventKey,
    AccountingEventKind Kind,
    DateTimeOffset OccurredAt,
    AccountingClassification Classification,
    IReadOnlyList<AccountingPosting> Lines)
{
    /// <summary>Whether the candidate rule of the request matches the event (null without a candidate).</summary>
    public bool? CandidateMatches { get; init; }

    /// <summary>The candidate's label pattern ran out of time.</summary>
    public bool CandidateTimedOut { get; init; }
}

/// <summary>
/// An entry that goes to an unclassified account (<c>classify list --unclassified</c>, NL-602 A3-T3).
/// </summary>
/// <param name="LedgerSeq">The event's ledger sequence.</param>
/// <param name="EventKey">The event key (what <c>classify set</c> takes).</param>
/// <param name="Kind">The event's kind.</param>
/// <param name="OccurredAt">When it happened.</param>
/// <param name="AmountMsat">The classifiable lines' amount (debit positive).</param>
/// <param name="Account">The unclassified account it goes to.</param>
/// <param name="Reason">Why (a default, a rule or an override that targets an unclassified account).</param>
/// <param name="Label">The event's label, if any.</param>
public sealed record AccountingUnclassifiedItem(
    long LedgerSeq,
    string EventKey,
    AccountingEventKind Kind,
    DateTimeOffset OccurredAt,
    long AmountMsat,
    string Account,
    string Reason,
    string? Label);

/// <summary>
/// A page of the unclassified listing (NL-602 A3-T3). The listing walks the projected operational entries in ledger
/// order and classifies each with the rules and overrides in effect now; a page reads at most a bounded number of
/// entries, so it may hold fewer items than asked and still have more after it.
/// </summary>
/// <param name="Items">The unclassified entries of the page.</param>
/// <param name="NextAfter">The ledger sequence to continue after.</param>
/// <param name="HasMore">Whether entries after <paramref name="NextAfter"/> remain to be looked at.</param>
/// <param name="Scanned">How many entries the page looked at.</param>
public sealed record AccountingUnclassifiedPage(
    IReadOnlyList<AccountingUnclassifiedItem> Items,
    long NextAfter,
    bool HasMore,
    int Scanned);