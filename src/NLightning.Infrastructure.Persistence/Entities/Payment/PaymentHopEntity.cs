// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Payment;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// One hop of an outgoing payment's route (<c>PaymentHop</c>), with the Sphinx shared secret the origin needs to
/// decrypt a returned error onion (ONION M4-T7).
/// </summary>
public class PaymentHopEntity
{
    /// <summary>
    /// The payment the hop belongs to.
    /// </summary>
    /// <remarks>This is part of the composite-key identifier</remarks>
    public required Hash PaymentHash { get; set; }

    /// <summary>
    /// The position in the route: 0 is our peer, the last is the payee.
    /// </summary>
    /// <remarks>This is part of the composite-key identifier</remarks>
    public required byte HopIndex { get; set; }

    /// <summary>
    /// The node of this hop.
    /// </summary>
    public required CompactPubKey NodeId { get; set; }

    /// <summary>
    /// The channel that reaches <see cref="NodeId"/>.
    /// </summary>
    public required ShortChannelId ShortChannelId { get; set; }

    /// <summary>
    /// The amount of the HTLC this hop receives, in millisatoshi.
    /// </summary>
    public required long AmountMsat { get; set; }

    /// <summary>
    /// The <c>cltv_expiry</c> of the HTLC this hop receives.
    /// </summary>
    public required uint CltvExpiry { get; set; }

    /// <summary>
    /// The 32-byte Sphinx shared secret of this hop.
    /// </summary>
    public required byte[] SharedSecret { get; set; }

    /// <summary>
    /// The hold time this hop reported in a verified <c>attribution_data</c> (BOLT 4), in milliseconds; null when none
    /// was verified for it (migration <c>AddAttributionData</c>).
    /// </summary>
    public long? HoldTimeMs { get; set; }

    // Default constructor for EF Core
    internal PaymentHopEntity()
    {
    }
}