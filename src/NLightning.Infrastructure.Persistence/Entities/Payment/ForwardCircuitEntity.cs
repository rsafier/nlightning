// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Payment;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// A forward we carry (<c>ForwardCircuitModel</c>, ONION M4-T7): the incoming HTLC and the outgoing HTLC that
/// continues it, keyed by the incoming side.
/// </summary>
/// <remarks>
/// Indexed by status (startup replay of unresolved circuits) and by the outgoing (channel, HTLC id) (resolution of a
/// downstream fulfill/fail). No foreign keys: a circuit is resolved through its own rows, independently of the
/// channel rows' lifetime.
/// </remarks>
public class ForwardCircuitEntity
{
    /// <summary>
    /// The channel of the incoming HTLC.
    /// </summary>
    /// <remarks>This is part of the composite-key identifier</remarks>
    public required ChannelId IncomingChannelId { get; set; }

    /// <summary>
    /// The id of the incoming HTLC (offered by the peer).
    /// </summary>
    /// <remarks>This is part of the composite-key identifier</remarks>
    public required ulong IncomingHtlcId { get; set; }

    /// <summary>
    /// The amount of the incoming HTLC, in millisatoshi.
    /// </summary>
    public required long IncomingAmountMsat { get; set; }

    /// <summary>
    /// The <c>cltv_expiry</c> of the incoming HTLC.
    /// </summary>
    public required uint IncomingCltvExpiry { get; set; }

    /// <summary>
    /// The 32-byte payment hash (the same on both sides).
    /// </summary>
    public required Hash PaymentHash { get; set; }

    /// <summary>
    /// The 32-byte shared secret of the incoming onion (wraps the error returned upstream).
    /// </summary>
    public required byte[] IncomingSharedSecret { get; set; }

    /// <summary>
    /// The <c>short_channel_id</c> the incoming onion asked us to forward to (real or alias).
    /// </summary>
    public required ShortChannelId OutgoingShortChannelId { get; set; }

    /// <summary>
    /// <c>amt_to_forward</c>, in millisatoshi.
    /// </summary>
    public required long OutgoingAmountMsat { get; set; }

    /// <summary>
    /// <c>outgoing_cltv_value</c>.
    /// </summary>
    public required uint OutgoingCltvExpiry { get; set; }

    /// <summary>
    /// When the circuit was created (stored as UTC ticks).
    /// </summary>
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// <c>ForwardCircuitStatus</c> (0 pending, 1 offered, 2 fulfilled, 3 failed).
    /// </summary>
    public required byte Status { get; set; }

    /// <summary>
    /// The channel the outgoing HTLC was offered on, once known.
    /// </summary>
    public ChannelId? OutgoingChannelId { get; set; }

    /// <summary>
    /// The id of the outgoing HTLC, once known.
    /// </summary>
    public ulong? OutgoingHtlcId { get; set; }

    /// <summary>
    /// When the circuit was resolved (stored as UTC ticks).
    /// </summary>
    public DateTimeOffset? ResolvedAt { get; set; }

    // Default constructor for EF Core
    internal ForwardCircuitEntity()
    {
    }
}