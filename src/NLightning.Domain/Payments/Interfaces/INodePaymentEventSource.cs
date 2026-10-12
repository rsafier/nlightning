namespace NLightning.Domain.Payments.Interfaces;

using Events;
using Signing;

/// <summary>Payment events attributed to the immutable node composition that produced them.</summary>
public interface INodePaymentEventSource
{
    NodeSigningContext Context { get; }
    INodePaymentEventSubscription Subscribe(int capacity = 1024);
}

public interface INodePaymentEventSubscription : IDisposable
{
    bool Overflowed { get; }
    IAsyncEnumerable<NodePaymentEvent> ReadAllAsync(CancellationToken cancellationToken = default);
}

public sealed record NodePaymentEvent(NodeSigningContext Context, PaymentEvent Event);