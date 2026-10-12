namespace NLightning.Application.Payments.Send.Interfaces;

using Domain.Channels.Commitments.Events;

/// <summary>
/// Completes our outgoing payments from the channel layer's outcome events (ABCD W2-C). The HTLC switch calls it for
/// every <see cref="OutgoingHtlcFulfilled"/> and <see cref="OutgoingHtlcFailed"/> of an HTLC whose origin is
/// <c>HtlcOrigin.Local</c> (or, before NL-250 persists origins, of any outgoing HTLC that has no forward circuit).
/// </summary>
/// <remarks>
/// <para>Implemented by <c>PaymentService</c>. Both methods are idempotent (events are replayed on startup): an event for
/// a payment that is already complete, or for an HTLC that belongs to no payment, changes nothing and returns false.
/// The outcome is saved (one <c>IUnitOfWork.SaveChangesAsync</c>) before the method returns, so the switch must call
/// it before it prunes the settled HTLC row on the <see cref="OutgoingHtlcSettled"/> that follows.</para>
/// <para>An event matches a payment when the payment for its hash is <c>InFlight</c> and either recorded this exact
/// HTLC (<c>OutgoingChannelId</c>/<c>OutgoingHtlcId</c>) or has not recorded one yet (a crash or a failed save around
/// the offer), the HTLC's stored origin is <c>HtlcOrigin.Local(hash)</c> or none (origins are stored only once NL-250
/// lands), its record in channel memory, when still there, matches the payment's first hop, and, for a failure, no other
/// non-final outgoing HTLC carries the hash. A fulfill whose preimage is right is recorded even when it matches no
/// in-flight attempt (the payment is <c>Failed</c> or recorded another HTLC): the preimage proves the payment.</para>
/// <para>Call it outside any channel lock, like the rest of the switch. It locks only the event's payment hash, so it
/// waits at most for a payment attempt of that same hash; still, dispatch the outcomes of different channels
/// independently rather than one after another behind a single slow call.</para>
/// </remarks>
public interface IPaymentOutcomeHandler
{
    /// <summary>
    /// The payee revealed the preimage: the payment succeeds once SHA256(preimage) equals its hash.
    /// </summary>
    /// <returns>True when the event completed one of our payments.</returns>
    Task<bool> HandleOutgoingHtlcFulfilledAsync(OutgoingHtlcFulfilled fulfilled,
                                                CancellationToken cancellationToken = default);

    /// <summary>
    /// The HTLC failed irrevocably: the error onion is decrypted with the payment's per-hop shared secrets, interpreted
    /// with <c>FailureInterpreter</c> and stored on the payment (code, erring hop index, reason). No retry.
    /// </summary>
    /// <returns>True when the event completed one of our payments.</returns>
    Task<bool> HandleOutgoingHtlcFailedAsync(OutgoingHtlcFailed failed, CancellationToken cancellationToken = default);

    /// <summary>
    /// Settles the <c>InFlight</c> payments that have no recorded HTLC id (a crash between the payment's save and the
    /// HTLC id's save, or before the offer): each is attached to its HTLC when exactly one non-final outgoing HTLC in
    /// channel memory matches its first hop (or carries its <c>HtlcOrigin.Local</c>), failed without a code when none
    /// does, and left in flight when that is ambiguous. Call it at startup once every channel is registered in memory
    /// (after <c>PeerManager.StartAsync</c> awaited <c>RegisterExistingChannelAsync</c>), after the replay of the
    /// pending channel events.
    /// </summary>
    /// <returns>How many payments were attached or failed.</returns>
    Task<int> ReconcileInFlightPaymentsAsync(CancellationToken cancellationToken = default);
}