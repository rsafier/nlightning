namespace NLightning.Domain.Payments.Interfaces;

using Events;

/// <summary>
/// Subscriptions to payment outcomes: invoices settled and payments succeeded or failed (Cashu plan C0, NL-991).
/// </summary>
/// <remarks>
/// <para>Events are kept in memory only, from the moment of the subscription: a consumer that must not miss an
/// outcome subscribes first and then reads the state it waits on from the database (an outcome committed in between
/// is then either in the read or in the subscription).</para>
/// <para>Each subscription has its own bounded queue; a subscriber that does not keep up loses the oldest events and
/// sees <see cref="IPaymentEventSubscription.Overflowed"/>, after which it must read the database again.</para>
/// </remarks>
public interface IPaymentEventSource
{
    /// <summary>
    /// Starts a subscription. Dispose it to stop.
    /// </summary>
    /// <param name="capacity">How many unread events it keeps (at least 1).</param>
    IPaymentEventSubscription Subscribe(int capacity = 1024);
}

/// <summary>
/// One subscription of <see cref="IPaymentEventSource"/>.
/// </summary>
public interface IPaymentEventSubscription : IDisposable
{
    /// <summary>
    /// True once an event was dropped because the queue was full.
    /// </summary>
    bool Overflowed { get; }

    /// <summary>
    /// The events in publication order, until the subscription is disposed or <paramref name="cancellationToken"/>
    /// is canceled.
    /// </summary>
    IAsyncEnumerable<PaymentEvent> ReadAllAsync(CancellationToken cancellationToken = default);
}