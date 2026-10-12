// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Payment;

using Domain.Channels.ValueObjects;

/// <summary>
/// One entry of the onion replay set (<c>OnionReplayEntry</c>, NL-078, migration <c>AddOnionReplaySet</c>): the packet
/// HMAC of an incoming payment onion, the incoming HTLC that recorded it and its <c>cltv_expiry</c>.
/// </summary>
/// <remarks>
/// Keyed by the HMAC and indexed by <see cref="ExpiryHeight"/> (pruning). No foreign key: an entry must outlive the
/// channel and HTLC rows until its expiry.
/// </remarks>
public class OnionReplayEntryEntity
{
    /// <summary>
    /// The 32-byte packet HMAC of the onion.
    /// </summary>
    /// <remarks>This is the primary key</remarks>
    public required byte[] Hmac { get; set; }

    /// <summary>
    /// The channel of the incoming HTLC that recorded the HMAC.
    /// </summary>
    public required ChannelId ChannelId { get; set; }

    /// <summary>
    /// The id of that incoming HTLC.
    /// </summary>
    public required ulong HtlcId { get; set; }

    /// <summary>
    /// The incoming HTLC's <c>cltv_expiry</c>: the entry is deleted once the chain is past it.
    /// </summary>
    public required uint ExpiryHeight { get; set; }

    // Default constructor for EF Core
    internal OnionReplayEntryEntity()
    {
    }
}