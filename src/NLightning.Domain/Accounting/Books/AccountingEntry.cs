namespace NLightning.Domain.Accounting.Books;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Enums;

/// <summary>
/// The books' entry for one sealed accounting event (one per <see cref="LedgerSeq"/>); its postings sum to zero. An event
/// that posts nothing (a memo, a failed payment, a marker) still gets an entry with no postings, so the books know they
/// saw it.
/// </summary>
public sealed record AccountingEntry(
    long LedgerSeq,
    string EventKey,
    AccountingEventKind Kind,
    DateTimeOffset OccurredAt,
    ChannelId? ChannelId,
    Hash? PaymentHash,
    IReadOnlyList<AccountingPosting> Postings,
    string? Note = null)
{
    public bool IsBalanced => Postings.Sum(p => p.AmountMsat) == 0;
}
