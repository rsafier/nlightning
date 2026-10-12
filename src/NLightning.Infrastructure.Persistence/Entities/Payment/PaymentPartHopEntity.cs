// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Payment;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// One hop of an offered payment part's route (<see cref="PaymentPartEntity"/>), with the Sphinx shared secret the
/// origin needs to decrypt that part's returned error onion after a restart (NL-321).
/// </summary>
public class PaymentPartHopEntity
{
    /// <summary>
    /// The payment the part belongs to.
    /// </summary>
    /// <remarks>This is part of the composite-key identifier</remarks>
    public required Hash PaymentHash { get; set; }

    /// <summary>
    /// The position of the part among its payment's parts.
    /// </summary>
    /// <remarks>This is part of the composite-key identifier</remarks>
    public required byte PartIndex { get; set; }

    /// <summary>
    /// The position in the part's route: 0 is our peer, the last is the payee.
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
    /// The hold time this hop reported in a verified <c>attribution_data</c> (BOLT 4) of the part's failure or
    /// fulfill, in milliseconds; null when none was verified for it.
    /// </summary>
    public long? HoldTimeMs { get; set; }

    // Default constructor for EF Core
    internal PaymentPartHopEntity()
    {
    }
}