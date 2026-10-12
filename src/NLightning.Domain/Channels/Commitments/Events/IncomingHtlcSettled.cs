namespace NLightning.Domain.Channels.Commitments.Events;

using Crypto.ValueObjects;
using Enums;
using ValueObjects;

/// <summary>
/// An HTLC the peer offered is gone for good (<see cref="HtlcState.SentRemoveAckRevocation"/>): our removal of it
/// (fulfill or failure) is irrevocably committed and its amount is folded into the balances. Nothing is left to do for
/// it, so consumers prune its archived record (NL-243).
/// </summary>
/// <remarks>The engine raises it exactly once, from <c>ReceiveCommit</c>: our <c>revoke_and_ack</c> of the last
/// commitment that still held the HTLC makes the removal final. The settled record stays pending (re-derived on
/// startup) until the persistence layer prunes it.</remarks>
/// <param name="ChannelId">The channel.</param>
/// <param name="HtlcId">The peer's id of the HTLC.</param>
/// <param name="PaymentHash">The payment hash.</param>
/// <param name="Kind">How we removed it.</param>
public sealed record IncomingHtlcSettled(ChannelId ChannelId, ulong HtlcId, Hash PaymentHash, HtlcRemovalKind Kind)
    : IChannelDomainEvent;