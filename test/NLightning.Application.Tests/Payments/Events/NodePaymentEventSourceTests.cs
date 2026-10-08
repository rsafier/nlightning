using NLightning.Application.Payments.Events;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Payments.Events;
using NLightning.Domain.Signing;

namespace NLightning.Application.Tests.Payments.Events;

public sealed class NodePaymentEventSourceTests
{
    [Fact]
    public async Task CollidingPaymentHashesRemainAttributedToSeparateNodeSubscriptions()
    {
        var pub = new CompactPubKey(Convert.FromHexString("0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798"));
        var a = new NodeSigningContext("node-a", "owner-a", "signer-a", "regtest", pub);
        var b = a with { NodeId = "node-b", OwnerId = "owner-b", SignerId = "signer-b" };
        var hubA = new PaymentEventHub();
        var hubB = new PaymentEventHub();
        var sourceA = new NodePaymentEventSource(a, hubA);
        var sourceB = new NodePaymentEventSource(b, hubB);
        using var subscriptionA = sourceA.Subscribe();
        using var subscriptionB = sourceB.Subscribe();
        var hash = new Hash(new byte[32]);
        hubA.Publish(new PaymentFailedEvent(hash, "failure-a", DateTimeOffset.UtcNow));
        hubB.Publish(new PaymentFailedEvent(hash, "failure-b", DateTimeOffset.UtcNow));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var eventsA = subscriptionA.ReadAllAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        await using var eventsB = subscriptionB.ReadAllAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await eventsA.MoveNextAsync());
        Assert.True(await eventsB.MoveNextAsync());
        Assert.Equal(a, eventsA.Current.Context);
        Assert.Equal(b, eventsB.Current.Context);
        Assert.Equal("failure-a", Assert.IsType<PaymentFailedEvent>(eventsA.Current.Event).Reason);
        Assert.Equal("failure-b", Assert.IsType<PaymentFailedEvent>(eventsB.Current.Event).Reason);
        Assert.Equal(eventsA.Current.Event.PaymentHash, eventsB.Current.Event.PaymentHash);
    }

    [Fact]
    public async Task ContextSubscriptionRetainsOverflowAndDisposalBehavior()
    {
        var pub = new CompactPubKey(Convert.FromHexString("0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798"));
        var context = new NodeSigningContext("node", "owner", "signer", "regtest", pub);
        var hub = new PaymentEventHub();
        var source = new NodePaymentEventSource(context, hub);
        using var subscription = source.Subscribe(1);
        var hash = new Hash(new byte[32]);
        hub.Publish(new PaymentFailedEvent(hash, "first", DateTimeOffset.UtcNow));
        hub.Publish(new PaymentFailedEvent(hash, "second", DateTimeOffset.UtcNow));
        Assert.True(subscription.Overflowed);
        subscription.Dispose();
        Assert.Equal(0, hub.SubscriberCount);
        await using var events = subscription.ReadAllAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await events.MoveNextAsync());
        Assert.Equal("second", Assert.IsType<PaymentFailedEvent>(events.Current.Event).Reason);
        Assert.False(await events.MoveNextAsync());
    }
}