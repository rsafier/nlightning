namespace NLightning.Application.Payments.Trampoline;

using Domain.Channels.Commitments.Events;

/// <summary>
/// Sends and drives the outgoing leg of a trampoline relay (NL-875): implemented by <c>PaymentService</c>, which reuses
/// its planner, MPP split, retries and per-part persistence. The leg is not awaited: its end is reported to the
/// registered <see cref="ITrampolineLegObserver"/>.
/// </summary>
public interface ITrampolineLegSender
{
    /// <summary>
    /// Persists the leg's <c>Payments</c> row (<c>IsTrampolineRelay</c>) and offers its first round, every HTLC with
    /// <c>HtlcOrigin.Trampoline(request.PaymentHash)</c>. Returns once the first round is offered (or the leg already
    /// exists: idempotent per payment hash). A leg that cannot start ends through
    /// <see cref="ITrampolineLegObserver.OnLegFailedAsync"/>.
    /// </summary>
    /// <remarks>
    /// Per payment hash: a leg in flight (a session of this process, or a stored <c>InFlight</c> relay row) is left
    /// alone; a stored <c>Succeeded</c> leg is reported again (<see cref="ITrampolineLegObserver.OnLegSucceededAsync"/>);
    /// a <c>Failed</c> relay row is replaced by a new leg (the relay's next attempt for the same hash, e.g. after the
    /// origin retried with a larger budget), so the engine must not call it to recover a <c>Sending</c> relay after a
    /// restart: the startup reconciliation and the switch's replays report those. The observer is called on the thread
    /// pool after the save that records the end, never under the sender's locks, so it may call
    /// <see cref="StartAsync"/> under its own.
    /// </remarks>
    Task StartAsync(TrampolineLegRequest request, CancellationToken cancellationToken);

    /// <summary>An outgoing HTLC of a leg was fulfilled (the relay engine forwards the switch's origin-3 event).</summary>
    Task HandleOutgoingFulfilledAsync(OutgoingHtlcFulfilled fulfilled, CancellationToken cancellationToken);

    /// <summary>An outgoing HTLC of a leg failed irrevocably (the relay engine forwards the switch's origin-3
    /// event).</summary>
    Task HandleOutgoingFailedAsync(OutgoingHtlcFailed failed, CancellationToken cancellationToken);
}