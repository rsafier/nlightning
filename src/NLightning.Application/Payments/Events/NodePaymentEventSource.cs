using System.Runtime.CompilerServices;

namespace NLightning.Application.Payments.Events;

using Domain.Payments.Interfaces;
using Domain.Signing;

/// <summary>Attributes events at the enrolled composition boundary without accepting caller-selected identities.</summary>
public sealed class NodePaymentEventSource : INodePaymentEventSource
{
    private readonly IPaymentEventSource _source;
    public NodeSigningContext Context { get; }

    public NodePaymentEventSource(NodeSigningContext context, IPaymentEventSource source)
    {
        context.Validate();
        Context = context;
        _source = source;
    }

    public INodePaymentEventSubscription Subscribe(int capacity = 1024) =>
        new Subscription(Context, _source.Subscribe(capacity));

    private sealed class Subscription(NodeSigningContext context, IPaymentEventSubscription inner)
        : INodePaymentEventSubscription
    {
        public bool Overflowed => inner.Overflowed;
        public async IAsyncEnumerable<NodePaymentEvent> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var paymentEvent in inner.ReadAllAsync(cancellationToken))
                yield return new NodePaymentEvent(context, paymentEvent);
        }
        public void Dispose() => inner.Dispose();
    }
}