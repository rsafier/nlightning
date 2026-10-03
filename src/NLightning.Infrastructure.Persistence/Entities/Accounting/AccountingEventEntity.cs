// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Accounting;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// One row of the accounting feed (<c>AccountingEventModel</c>, NL-602).
/// </summary>
/// <remarks>
/// No unique constraint on <see cref="EventKey"/> on purpose: the row rides in a core save that must never fail
/// because of it; the sealer marks a repeated key as a duplicate instead. No foreign keys: the feed outlives every
/// row it names.
/// </remarks>
public class AccountingEventEntity
{
    /// <summary>Storage id (insert order).</summary>
    public long Id { get; set; }

    public required string EventKey { get; set; }

    /// <summary><c>AccountingEventKind</c>.</summary>
    public required int Kind { get; set; }

    /// <summary>When the fact happened (stored as UTC ticks).</summary>
    public required DateTimeOffset OccurredAt { get; set; }

    public uint? BlockHeight { get; set; }
    public ChannelId? ChannelId { get; set; }
    public ShortChannelId? ShortChannelId { get; set; }
    public Hash? PaymentHash { get; set; }
    public TxId? TxId { get; set; }
    public uint? OutputIndex { get; set; }
    public CompactPubKey? Counterparty { get; set; }

    /// <summary>Our balance change, signed msat.</summary>
    public required long AmountMsat { get; set; }

    /// <summary>The fee we paid, msat.</summary>
    public required long FeeMsat { get; set; }

    /// <summary><c>AccountingFinality</c>.</summary>
    public required byte Finality { get; set; }

    /// <summary><c>AccountingEventFlags</c>.</summary>
    public required int Flags { get; set; }

    /// <summary>The details as a flat JSON object of strings (<c>AccountingDetailsCodec</c>), or null.</summary>
    public string? Details { get; set; }

    /// <summary>The dense ledger sequence, null until sealed (and for duplicates).</summary>
    public long? LedgerSeq { get; set; }

    /// <summary>The 32-byte chain hash, null until sealed.</summary>
    public byte[]? Hash { get; set; }

    // Default constructor for EF Core
    internal AccountingEventEntity()
    {
    }
}