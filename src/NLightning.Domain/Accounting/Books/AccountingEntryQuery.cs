namespace NLightning.Domain.Accounting.Books;

using Channels.ValueObjects;
using Enums;

/// <summary>A page of book entries in ledger order (then adjustment order).</summary>
public sealed record AccountingEntryQuery(
    long AfterLedgerSeq,
    int Take,
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null,
    IReadOnlyCollection<AccountingEventKind>? Kinds = null,
    ChannelId? ChannelId = null,
    AccountRole? Account = null)
{
    /// <summary>The book to read (the operational one unless set).</summary>
    public AccountingBook Book { get; init; } = AccountingBook.Operational;

    /// <summary>
    /// With <see cref="AfterLedgerSeq"/>, where the page starts: entries after (<see cref="AfterLedgerSeq"/>,
    /// <see cref="AfterAdjustment"/>). The default (<see cref="int.MaxValue"/>) starts after every adjustment of
    /// <see cref="AfterLedgerSeq"/>; a financial reader paging through adjustments passes the last one it read.
    /// </summary>
    public int AfterAdjustment { get; init; } = int.MaxValue;

    /// <summary>Only the entries of this closed period (A3-T5), when set.</summary>
    public string? ClosedPeriodId { get; init; }

    /// <summary>Only the entries with every one of these flags, when not <see cref="AccountingEntryFlags.None"/>.</summary>
    public AccountingEntryFlags WithFlags { get; init; }
}