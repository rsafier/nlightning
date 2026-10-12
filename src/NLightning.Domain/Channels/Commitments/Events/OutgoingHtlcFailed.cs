namespace NLightning.Domain.Channels.Commitments.Events;

using Crypto.ValueObjects;
using Enums;
using ValueObjects;

/// <summary>
/// The failure of an HTLC we offered is irrevocably committed (<see cref="HtlcState.RcvdRemoveAckRevocation"/>). Only
/// now may the upstream HTLC be failed (BOLT 2: "until the corresponding HTLC is irrevocably removed from the outgoing
/// channel", B2-FWD-02).
/// </summary>
/// <remarks>The engine raises it exactly once, on the <c>revoke_and_ack</c> that makes the removal final. It is never
/// raised for a failure that is still pending or that a disconnect reverted.</remarks>
/// <param name="ChannelId">The channel.</param>
/// <param name="HtlcId">Our id of the HTLC.</param>
/// <param name="PaymentHash">The payment hash.</param>
/// <param name="Removal">The failure: <see cref="HtlcRemovalKind.Fail"/> with the opaque reason, or
/// <see cref="HtlcRemovalKind.FailMalformed"/> with the failure code and the onion hash.</param>
public sealed record OutgoingHtlcFailed(ChannelId ChannelId, ulong HtlcId, Hash PaymentHash, HtlcRemoval Removal)
    : IChannelDomainEvent;