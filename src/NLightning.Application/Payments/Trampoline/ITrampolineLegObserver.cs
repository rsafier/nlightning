namespace NLightning.Application.Payments.Trampoline;

using Domain.Crypto.ValueObjects;
using Domain.Money;

/// <summary>
/// Learns how the outgoing leg of a trampoline relay ended (NL-875): implemented by the relay engine
/// (<c>TrampolineRelayService</c>), called by <see cref="ITrampolineLegSender"/>. Calls may repeat after a restart, so
/// implementations are idempotent.
/// </summary>
public interface ITrampolineLegObserver
{
    /// <summary>The leg succeeded: <paramref name="preimage"/> settles every incoming part.</summary>
    /// <param name="paymentHash">The relayed payment's hash.</param>
    /// <param name="preimage">The preimage the next node revealed.</param>
    /// <param name="totalSent">What the leg's fulfilled HTLCs carried in total (amount plus routing fees).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task OnLegSucceededAsync(Hash paymentHash, Secret preimage, LightningMoney totalSent,
                             CancellationToken cancellationToken);

    /// <summary>The leg failed for good: no HTLC of it is in flight any more.</summary>
    Task OnLegFailedAsync(Hash paymentHash, TrampolineLegFailure failure, CancellationToken cancellationToken);
}