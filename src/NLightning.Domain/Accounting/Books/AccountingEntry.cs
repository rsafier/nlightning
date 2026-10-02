namespace NLightning.Domain.Accounting.Books;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Enums;

/// <summary>
/// The books' entry for one sealed accounting event (one per <see cref="LedgerSeq"/> and <see cref="Book"/>); its
/// postings sum to zero. An event that posts nothing (a memo, a failed payment, a marker) still gets an entry with no
/// postings, so the books know they saw it.
/// </summary>
/// <remarks>
/// The financial book (D-A7) has one entry per operational entry (<see cref="Adjustment"/> 0) and may add adjustments
/// of it later (<see cref="Adjustment"/> 1, 2, ...; D-A8), each dated in the open period. The key of an entry is
/// (<see cref="Book"/>, <see cref="LedgerSeq"/>, <see cref="Adjustment"/>).
/// </remarks>
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

    /// <summary>The book of the entry (D-A7).</summary>
    public AccountingBook Book { get; init; } = AccountingBook.Operational;

    /// <summary>0 for the entry projected from the event; n for the n-th adjustment of it (D-A8).</summary>
    public int Adjustment { get; init; }

    public AccountingEntryFlags Flags { get; init; }

    /// <summary>Why a financial entry went to its account (A3-T3); null in the operational book.</summary>
    public AccountingClassificationSource? Classification { get; init; }

    /// <summary>The <c>AccountingRules</c> id that classified the entry, when <see cref="Classification"/> is
    /// <see cref="AccountingClassificationSource.Rule"/>.</summary>
    public long? RuleId { get; init; }

    /// <summary>The closed period that holds the entry (A3-T5), or null while its period is open.</summary>
    public string? ClosedPeriodId { get; init; }
}