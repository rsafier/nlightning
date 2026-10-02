// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Accounting;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// The books' entry for one sealed accounting event (<c>AccountingEntry</c>, NL-602 A2); its postings are the
/// <see cref="AccountingPostingEntity"/> rows with the same <see cref="LedgerSeq"/>.
/// </summary>
/// <remarks>
/// The projector is the table's only writer, so <see cref="EventKey"/> is unique here (the feed marks a repeated key as
/// a duplicate and never seals it). The table is a projection of the feed: <c>accounting rebuild</c> clears and
/// regenerates it.
/// </remarks>
public class AccountingEntryEntity
{
    /// <summary>The event's ledger sequence (assigned by the sealer, never generated here).</summary>
    public long LedgerSeq { get; set; }

    public required string EventKey { get; set; }

    /// <summary><c>AccountingEventKind</c>.</summary>
    public required int Kind { get; set; }

    /// <summary>When the event happened (stored as UTC ticks).</summary>
    public required DateTimeOffset OccurredAt { get; set; }

    public ChannelId? ChannelId { get; set; }
    public Hash? PaymentHash { get; set; }
    public string? Note { get; set; }

    // Default constructor for EF Core
    internal AccountingEntryEntity()
    {
    }
}