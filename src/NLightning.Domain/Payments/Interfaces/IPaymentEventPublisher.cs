namespace NLightning.Domain.Payments.Interfaces;

using Events;

/// <summary>
/// Raises payment state changes to the subscribers of <see cref="IPaymentEventSource"/> (Cashu plan C0, NL-991).
/// </summary>
/// <remarks>
/// Call it only after the save that recorded the outcome has committed, and never inside a lock a subscriber could
/// need: it never blocks and never throws (a full subscriber loses its oldest event and is flagged
/// <see cref="IPaymentEventSubscription.Overflowed"/>).
/// </remarks>
public interface IPaymentEventPublisher
{
    /// <summary>
    /// Hands <paramref name="paymentEvent"/> to every current subscriber.
    /// </summary>
    void Publish(PaymentEvent paymentEvent);
}