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
    Task StartAsync(TrampolineLegRequest request, CancellationToken cancellationToken);

    /// <summary>An outgoing HTLC of a leg was fulfilled (the relay engine forwards the switch's origin-3 event).</summary>
    Task HandleOutgoingFulfilledAsync(OutgoingHtlcFulfilled fulfilled, CancellationToken cancellationToken);

    /// <summary>An outgoing HTLC of a leg failed irrevocably (the relay engine forwards the switch's origin-3
    /// event).</summary>
    Task HandleOutgoingFailedAsync(OutgoingHtlcFailed failed, CancellationToken cancellationToken);
}