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
public sealed record OutgoingHtlcFulfilled(ChannelId ChannelId, ulong HtlcId, Hash PaymentHash, Secret PaymentPreimage)
    : IChannelDomainEvent;