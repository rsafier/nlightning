namespace NLightning.Domain.Payments.ValueObjects;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Enums;

/// <summary>
/// The reference an outgoing HTLC carries back to what caused it: our own payment, or the incoming HTLC it forwards
/// (the circuit key, ONION M4-T7).
/// </summary>
/// <remarks>
/// <c>IChannelOperations.OfferHtlcAsync</c> persists the origin in the same save as the add, so after a restart the
/// resolution of the outgoing HTLC (fulfill or irrevocable fail) can always be routed back: to the payment for
/// <see cref="HtlcOriginKind.Local"/>, upstream to (<see cref="IncomingChannelId"/>, <see cref="IncomingHtlcId"/>)
/// for <see cref="HtlcOriginKind.Forwarded"/>.
/// </remarks>
public readonly record struct HtlcOrigin
{
    public HtlcOriginKind Kind { get; }

    /// <summary>
    /// The payment hash of our payment (set for <see cref="HtlcOriginKind.Local"/>).
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
    /// True for an origin built by <see cref="Local"/> or <see cref="Forwarded"/>; false for <c>default</c>, which
    /// routes nowhere. <c>IChannelOperations.OfferHtlcAsync</c> refuses an invalid origin.
    /// </summary>
    public bool IsValid => Kind switch
    {
        HtlcOriginKind.Local => PaymentHash is not null,
        HtlcOriginKind.Forwarded => IncomingChannelId is not null && IncomingHtlcId is not null,
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
}