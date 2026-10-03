namespace NLightning.Application.Payments.Switch;

using Domain.Channels.Commitments.Events;
using Domain.Crypto.ValueObjects;
using Domain.Payments.Trampoline;

/// <summary>
/// Receives, from <see cref="HtlcSwitch"/>, the events of the HTLCs of trampoline relays (NL-875): the resolutions of
/// the outgoing HTLCs that carry <c>HtlcOrigin.Trampoline(paymentHash)</c>, and the replayed lock-ins of incoming HTLCs
/// that are already parts of a relay. The relay engine (<c>TrampolineRelayService</c>) implements it; the switch never
/// hands these events to an <see cref="ILocalPaymentHtlcHandler"/>, and does nothing with them when no handler is
/// registered (the HTLC deadline monitor and the BOLT 5 resolvers keep the incoming parts safe).
/// </summary>
/// <remarks>
/// <para>Register one implementation as a singleton. It runs outside every channel lock, may be called concurrently for
/// different HTLCs of one relay, and must be idempotent: events are replayed on startup and after a reestablish until
/// the settled outgoing record is pruned.</para>
/// <para>Safety rules the engine keeps (mirroring a forward): once any outgoing HTLC of a relay is fulfilled, persist
/// the preimage on every incoming part's record (<c>KnownPreimage</c>) and the relay <c>Fulfilled</c> in one save, then
/// fulfill every part; never fail an incoming part while an outgoing HTLC of its relay is unresolved (a failure is
/// final only once its removal is irrevocable, which is when <see cref="HandleFailedAsync"/> is called).</para>
/// <para>A handler that throws keeps the outgoing record (and so the event) for the next replay, as for a local
/// payment.</para>
/// </remarks>
public interface ITrampolineHtlcHandler
{
    /// <summary>
    /// The next node revealed the preimage of an outgoing HTLC of the relay with <paramref name="paymentHash"/> (live,
    /// or found on chain by the BOLT 5 resolvers, which persist it on the outgoing record first).
    /// </summary>
    /// <param name="fulfilled">The event (outgoing channel, HTLC id, hash, preimage, attribution).</param>
    /// <param name="paymentHash">The relay's payment hash, from the HTLC's stored origin.</param>
    /// <param name="cancellationToken">Cancels the handling.</param>
    Task HandleFulfilledAsync(OutgoingHtlcFulfilled fulfilled, Hash paymentHash, CancellationToken cancellationToken);

    /// <summary>
    /// An outgoing HTLC of the relay failed irrevocably (or timed out on chain, <c>HtlcRemovalKind.OnchainTimeout</c>):
    /// <see cref="OutgoingHtlcFailed.Removal"/> carries the error onion (decrypt it with the outgoing payment's hop
    /// shared secrets) or the malformed code of the first hop.
    /// </summary>
    /// <param name="failed">The event.</param>
    /// <param name="paymentHash">The relay's payment hash, from the HTLC's stored origin.</param>
    /// <param name="cancellationToken">Cancels the handling.</param>
    Task HandleFailedAsync(OutgoingHtlcFailed failed, Hash paymentHash, CancellationToken cancellationToken);

    /// <summary>
    /// An outgoing HTLC of the relay reached its final state. Answer true when its archived record may be pruned:
    /// nothing will need to replay its resolution (the relay recorded it). The switch prunes only when this answers
    /// true <b>and</b> the relay is completed with every incoming part removed (or gone from a loaded channel).
    /// </summary>
    /// <param name="settled">The event.</param>
    /// <param name="paymentHash">The relay's payment hash, from the HTLC's stored origin.</param>
    /// <param name="cancellationToken">Cancels the handling.</param>
    Task<bool> HandleSettledAsync(OutgoingHtlcSettled settled, Hash paymentHash, CancellationToken cancellationToken);

    /// <summary>
    /// An incoming HTLC that is already a part of a relay was locked in again (a replay at startup, after a reestablish,
    /// or the BOLT 5 resolvers' final-hop decision on a channel closing on chain). The switch neither peels its onion
    /// again nor fails it: the relay decides (fulfill it from the relay's preimage, fail it once the relay failed and no
    /// outgoing HTLC is unresolved, or wait).
    /// </summary>
    /// <param name="lockedIn">The event (incoming channel and HTLC record).</param>
    /// <param name="part">The stored relay part of that HTLC.</param>
    /// <param name="cancellationToken">Cancels the handling.</param>
    Task HandleIncomingPartLockedInAsync(IncomingHtlcLockedIn lockedIn, TrampolineRelayPartModel part,
                                         CancellationToken cancellationToken);
}