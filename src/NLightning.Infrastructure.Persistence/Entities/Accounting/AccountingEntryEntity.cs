// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Accounting;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// A books entry (<c>AccountingEntry</c>, NL-602 A2; the financial book since migration <c>AddAccountingFinancial</c>,
/// A3-T0): the entry of one sealed accounting event in one book; its postings are the
/// <see cref="AccountingPostingEntity"/> rows with the same (<see cref="Book"/>, <see cref="LedgerSeq"/>,
/// <see cref="Adjustment"/>).
/// </summary>
/// <remarks>
/// <para>Key (<see cref="Book"/>, <see cref="LedgerSeq"/>, <see cref="Adjustment"/>). The operational book (0) has one
/// entry per sealed event (<see cref="Adjustment"/> 0) and is a projection of the feed: <c>accounting rebuild</c>
/// clears and regenerates it. The financial book (1, D-A7) has one entry per operational entry and, after a period
/// close, adjustments of it (<see cref="Adjustment"/> 1, 2, ...) dated in the open period (D-A8).</para>
/// <para>Each book's projector is its only writer, so (<see cref="Book"/>, <see cref="EventKey"/>,
/// <see cref="Adjustment"/>) is unique (the feed marks a repeated key as a duplicate and never seals it).</para>
/// </remarks>
public class AccountingEntryEntity
{
    /// <summary><c>AccountingBook</c> (0 operational, 1 financial); existing rows 0.</summary>
    public byte Book { get; set; }

    /// <summary>The event's ledger sequence (assigned by the sealer, never generated here).</summary>
    public long LedgerSeq { get; set; }

    /// <summary>0 for the entry projected from the event, n for its n-th adjustment (financial book).</summary>
    public int Adjustment { get; set; }

    public required string EventKey { get; set; }

    /// <summary><c>AccountingEventKind</c>.</summary>
    public required int Kind { get; set; }

    /// <summary>When the entry is dated (UTC ticks): the event's time, or the adjustment's.</summary>
    public required DateTimeOffset OccurredAt { get; set; }

    public ChannelId? ChannelId { get; set; }
    public Hash? PaymentHash { get; set; }
    public string? Note { get; set; }

    /// <summary><c>AccountingEntryFlags</c>; existing rows 0.</summary>
    public int Flags { get; set; }

    /// <summary><c>AccountingClassificationSource</c> of a financial entry (A3-T3); null in the operational book.</summary>
    public byte? Classification { get; set; }

    /// <summary>The <c>AccountingRules</c> id that classified a financial entry (no foreign key: a removed rule leaves
    /// its entries as they were).</summary>
    public long? RuleId { get; set; }

    /// <summary>The <c>AccountingPeriods</c> id of the closed period that holds the entry (A3-T5), or null.</summary>
    public string? ClosedPeriodId { get; set; }

    // Default constructor for EF Core
    internal AccountingEntryEntity()
    {
    }
}