namespace NLightning.Domain.Channels.Commitments.Events;

using Crypto.ValueObjects;
using ValueObjects;

/// <summary>
/// The peer revealed the preimage of an HTLC we offered (<c>update_fulfill_htlc</c>). A preimage is final knowledge:
/// the upstream HTLC is fulfilled immediately, without waiting for the removal to be committed (B2-FWD-05).
/// </summary>
/// <remarks>The engine raises it the first time the preimage is learnt. A fulfill the peer re-sends after a reconnection
/// does not raise it again, because <see cref="HtlcRecord.KnownPreimage"/> survives
/// <see cref="ChannelCommitments.RevertUncommitted"/>. The preimage must be persisted before the event is raised
/// (I10). The event stays pending (re-derived on startup) while the HTLC record exists.</remarks>
/// <param name="ChannelId">The channel.</param>
/// <param name="HtlcId">Our id of the HTLC.</param>
/// <param name="PaymentHash">The payment hash.</param>
/// <param name="PaymentPreimage">The preimage (hashes to <paramref name="PaymentHash"/>).</param>
/// <param name="AttributionData">The <c>attribution_data</c> of the peer's <c>update_fulfill_htlc</c> (BOLT 4 hold
/// times, 920 bytes), empty when it carried none or the fulfill is no longer stored (a fulfill reverted by a
/// disconnection keeps only its preimage, and an on-chain claim has none). A forward wraps it upstream; the origin
/// verifies it.</param>
/// <param name="FulfillmentPayload">The <c>fulfillment_payload</c> of the peer's <c>update_fulfill_htlc</c>, empty when
/// none.</param>
public sealed record OutgoingHtlcFulfilled(
    ChannelId ChannelId,
    ulong HtlcId,
    Hash PaymentHash,
    Secret PaymentPreimage,
    ReadOnlyMemory<byte> AttributionData = default,
    ReadOnlyMemory<byte> FulfillmentPayload = default) : IChannelDomainEvent;