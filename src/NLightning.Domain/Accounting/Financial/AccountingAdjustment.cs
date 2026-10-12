namespace NLightning.Domain.Accounting.Financial;

using Books;
using Channels.ValueObjects;
using Crypto.ValueObjects;
using Enums;

/// <summary>
/// A correction of a closed period's fact, handed to <see cref="IAccountingAdjustmentSink.StageAdjustmentAsync"/> (A3-T5,
/// D-A8): the sink stages it as an adjustment entry of the financial book in the open period, dated now.
/// </summary>
/// <param name="Reason">Why.</param>
/// <param name="LedgerSeq">The ledger sequence of the fact (the operational entry it derives from).</param>
/// <param name="EventKey">The fact's event key.</param>
/// <param name="Kind">The fact's kind.</param>
/// <param name="FactOccurredAt">When the fact happened (in the closed period).</param>
/// <param name="Postings">The correcting lines (financial: every line names its account); they must balance in msat.
/// For <see cref="AccountingAdjustmentReason.LateFact"/>, the whole entry the projector would have posted.</param>
public sealed record AccountingAdjustment(
    AccountingAdjustmentReason Reason,
    long LedgerSeq,
    string EventKey,
    AccountingEventKind Kind,
    DateTimeOffset FactOccurredAt,
    IReadOnlyList<AccountingPosting> Postings)
{
    public ChannelId? ChannelId { get; init; }
    public Hash? PaymentHash { get; init; }

    /// <summary>Free text after the sink's own note.</summary>
    public string? Note { get; init; }

    /// <summary>
    /// Makes the adjustment idempotent: when an adjustment of <see cref="EventKey"/> with this key exists (saved or
    /// staged), nothing is staged again. For example <c>price:{ledgerSeq}:{adjustment}:{index}</c> or
    /// <c>override:{account}</c>. Ignored for <see cref="AccountingAdjustmentReason.LateFact"/>, which is staged only when
    /// the book has no entry of the event key at all.
    /// </summary>
    public string? DedupeKey { get; init; }

    /// <summary>Flags the entry carries besides <see cref="AccountingEntryFlags.Adjustment"/> (unclassified,
    /// unvalued).</summary>
    public AccountingEntryFlags Flags { get; init; }

    public AccountingClassificationSource? Classification { get; init; }
    public long? RuleId { get; init; }
}