namespace NLightning.Application.Payments.Switch;

using Domain.Channels.Commitments.Events;
using Domain.Crypto.ValueObjects;

/// <summary>
/// Receives, from <see cref="HtlcSwitch"/>, the resolutions of the HTLCs we offered for our own payments
/// (<c>HtlcOrigin.Local</c>): the send path (ABCD W2-C <c>PaymentService</c>) records the preimage or decrypts the
/// failure for the payment.
/// </summary>
/// <remarks>
/// <para>Register implementations as singletons (every registered one is called, in registration order). They run
/// outside every channel lock.</para>
/// <para>Events are replayed on startup (and after a reestablish) until the settled HTLC record is pruned, so both
/// methods must be idempotent. The switch prunes the record on <c>OutgoingHtlcSettled</c> only when every handler
/// returned for the HTLC's fulfilled/failed event in this process; a handler that throws keeps the record (and so the
/// event) for the next replay.</para>
/// </remarks>
public interface ILocalPaymentHtlcHandler
{
    /// <summary>
    /// The payee revealed the preimage of our HTLC (final knowledge: the payment succeeded).
    /// </summary>
    /// <param name="fulfilled">The event (channel, our HTLC id, hash, preimage).</param>
    /// <param name="paymentHash">The payment hash stored as the HTLC's origin.</param>
    /// <param name="cancellationToken">Cancels the handling.</param>
    Task HandleFulfilledAsync(OutgoingHtlcFulfilled fulfilled, Hash paymentHash, CancellationToken cancellationToken);

    /// <summary>
    /// Our HTLC failed irrevocably: <see cref="OutgoingHtlcFailed.Removal"/> carries the error onion (decrypt it with
    /// the payment's hop shared secrets) or the malformed code of our first hop.
    /// </summary>
    /// <param name="failed">The event.</param>
    /// <param name="paymentHash">The payment hash stored as the HTLC's origin.</param>
    /// <param name="cancellationToken">Cancels the handling.</param>
    Task HandleFailedAsync(OutgoingHtlcFailed failed, Hash paymentHash, CancellationToken cancellationToken);
}