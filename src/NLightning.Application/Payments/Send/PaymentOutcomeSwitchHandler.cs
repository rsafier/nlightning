namespace NLightning.Application.Payments.Send;

using Domain.Channels.Commitments.Events;
using Domain.Crypto.ValueObjects;
using Interfaces;
using Switch;

/// <summary>
/// Connects the HTLC switch (ABCD W2-B) to the send path (W2-C): every resolution of an HTLC we offered for our own
/// payment (<c>HtlcOrigin.Local</c>) goes to <see cref="IPaymentOutcomeHandler"/>.
/// </summary>
/// <remarks>
/// The outcome handler saves the payment's outcome before it returns and throws only when that failed, so the switch
/// keeps the settled HTLC row (and replays the event) exactly when the payment was not updated. An event that matches no
/// payment returns false and is not an error.
/// </remarks>
public sealed class PaymentOutcomeSwitchHandler : ILocalPaymentHtlcHandler
{
    private readonly IPaymentOutcomeHandler _outcomeHandler;

    public PaymentOutcomeSwitchHandler(IPaymentOutcomeHandler outcomeHandler)
    {
        _outcomeHandler = outcomeHandler;
    }

    /// <inheritdoc/>
    public Task HandleFulfilledAsync(OutgoingHtlcFulfilled fulfilled, Hash paymentHash,
                                     CancellationToken cancellationToken) =>
        _outcomeHandler.HandleOutgoingHtlcFulfilledAsync(fulfilled, cancellationToken);

    /// <inheritdoc/>
    public Task HandleFailedAsync(OutgoingHtlcFailed failed, Hash paymentHash, CancellationToken cancellationToken) =>
        _outcomeHandler.HandleOutgoingHtlcFailedAsync(failed, cancellationToken);
}