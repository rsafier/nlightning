namespace NLightning.Domain.Channels.Commitments.Events;

using Crypto.ValueObjects;
using Enums;
using ValueObjects;

/// <summary>
/// An HTLC we offered is gone for good (<see cref="HtlcState.RcvdRemoveAckRevocation"/>): its removal is irrevocably
/// committed and its amount is folded into the balances. Consumers use it to prune circuits and payment records.
/// </summary>
/// <remarks>The engine raises it exactly once, on the <c>revoke_and_ack</c> that makes the removal final, after
/// <see cref="OutgoingHtlcFailed"/> for a failure. The settled record stays pending (re-derived on startup) until the
/// persistence layer prunes it.</remarks>
/// <param name="ChannelId">The channel.</param>
/// <param name="HtlcId">Our id of the HTLC.</param>
/// <param name="PaymentHash">The payment hash.</param>
/// <param name="Kind">How it was removed.</param>
public sealed record OutgoingHtlcSettled(ChannelId ChannelId, ulong HtlcId, Hash PaymentHash, HtlcRemovalKind Kind)
    : IChannelDomainEvent;