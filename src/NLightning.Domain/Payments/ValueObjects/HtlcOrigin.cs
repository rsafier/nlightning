namespace NLightning.Domain.Payments.ValueObjects;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Enums;

/// <summary>
/// The reference an outgoing HTLC carries back to what caused it: our own payment, the incoming HTLC it forwards
/// (the circuit key, ONION M4-T7), or the trampoline relay it pays for (NL-875).
/// </summary>
/// <remarks>
/// <c>IChannelOperations.OfferHtlcAsync</c> persists the origin in the same save as the add, so after a restart the
/// resolution of the outgoing HTLC (fulfill or irrevocable fail) can always be routed back: to the payment for
/// <see cref="HtlcOriginKind.Local"/>, upstream to (<see cref="IncomingChannelId"/>, <see cref="IncomingHtlcId"/>)
/// for <see cref="HtlcOriginKind.Forwarded"/>, to the incoming parts of the trampoline relay with
/// <see cref="PaymentHash"/> for <see cref="HtlcOriginKind.Trampoline"/>.
/// </remarks>
public readonly record struct HtlcOrigin
{
    public HtlcOriginKind Kind { get; }

    /// <summary>
    /// The payment hash of our payment (set for <see cref="HtlcOriginKind.Local"/>) or of the trampoline relay (set for
    /// <see cref="HtlcOriginKind.Trampoline"/>).
    /// </summary>
    public Hash? PaymentHash { get; }

    /// <summary>
    /// The channel of the incoming HTLC (set for <see cref="HtlcOriginKind.Forwarded"/>).
    /// </summary>
    public ChannelId? IncomingChannelId { get; }

    /// <summary>
    /// The id of the incoming HTLC (set for <see cref="HtlcOriginKind.Forwarded"/>).
    /// </summary>
    public ulong? IncomingHtlcId { get; }

    /// <summary>
    /// True for an origin built by <see cref="Local"/>, <see cref="Forwarded"/> or <see cref="Trampoline"/>; false for
    /// <c>default</c>, which routes nowhere. <c>IChannelOperations.OfferHtlcAsync</c> refuses an invalid origin.
    /// </summary>
    public bool IsValid => Kind switch
    {
        HtlcOriginKind.Local => PaymentHash is not null,
        HtlcOriginKind.Forwarded => IncomingChannelId is not null && IncomingHtlcId is not null,
        HtlcOriginKind.Trampoline => PaymentHash is not null,
        _ => false
    };

    private HtlcOrigin(HtlcOriginKind kind, Hash? paymentHash, ChannelId? incomingChannelId, ulong? incomingHtlcId)
    {
        Kind = kind;
        PaymentHash = paymentHash;
        IncomingChannelId = incomingChannelId;
        IncomingHtlcId = incomingHtlcId;
    }

    /// <summary>
    /// An HTLC for one of our own payments.
    /// </summary>
    public static HtlcOrigin Local(Hash paymentHash) => new(HtlcOriginKind.Local, paymentHash, null, null);

    /// <summary>
    /// An HTLC that forwards the incoming HTLC <paramref name="incomingHtlcId"/> on
    /// <paramref name="incomingChannelId"/>.
    /// </summary>
    public static HtlcOrigin Forwarded(ChannelId incomingChannelId, ulong incomingHtlcId) =>
        new(HtlcOriginKind.Forwarded, null, incomingChannelId, incomingHtlcId);

    /// <summary>
    /// An HTLC of the outgoing payment of the trampoline relay with <paramref name="paymentHash"/> (NL-875): its
    /// resolution goes to the relay, which answers for every incoming part of that relay at once.
    /// </summary>
    public static HtlcOrigin Trampoline(Hash paymentHash) => new(HtlcOriginKind.Trampoline, paymentHash, null, null);
}