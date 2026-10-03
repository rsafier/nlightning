namespace NLightning.Application.Tests.Payments.Events;

using Application.Payments.Events;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Events;
using Domain.Payments.Interfaces;

public class PaymentEventHubTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Given_TwoSubscribers_When_AnEventIsPublished_Then_BothReadIt()
    {
        // Arrange
        var hub = new PaymentEventHub();
        using var first = hub.Subscribe();
        using var second = hub.Subscribe();
        var settled = Settled(1);

        // Act
        hub.Publish(settled);

        // Assert
        Assert.Same(settled, await ReadOneAsync(first));
        Assert.Same(settled, await ReadOneAsync(second));
        Assert.False(first.Overflowed);
    }

    [Fact]
    public async Task Given_ADisposedSubscription_When_AnEventIsPublished_Then_ItIsNotKeptAndTheReadEnds()
    {
        // Arrange
        var hub = new PaymentEventHub();
        var subscription = hub.Subscribe();

        // Act
        subscription.Dispose();
        hub.Publish(Settled(1));

        // Assert
        Assert.Equal(0, hub.SubscriberCount);
        await foreach (var _ in subscription.ReadAllAsync(TestContext.Current.CancellationToken))
            Assert.Fail("A disposed subscription reads nothing");
    }

    [Fact]
    public async Task Given_AFullQueue_When_AnotherEventIsPublished_Then_TheOldestIsDroppedAndOverflowedIsSet()
    {
        // Arrange
        var hub = new PaymentEventHub();
        using var subscription = hub.Subscribe(capacity: 2);

        // Act
        hub.Publish(Settled(1));
        hub.Publish(Settled(2));
        hub.Publish(Settled(3));

        // Assert: the newest two remain, in order
        Assert.True(subscription.Overflowed);
        Assert.Equal(Hash(2), (await ReadOneAsync(subscription)).PaymentHash);
        Assert.Equal(Hash(3), (await ReadOneAsync(subscription)).PaymentHash);
    }

    [Fact]
    public void Given_ACapacityBelowOne_When_Subscribing_Then_Throws()
    {
        // Arrange
        var hub = new PaymentEventHub();

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => hub.Subscribe(0));
    }

    internal static async Task<PaymentEvent> ReadOneAsync(IPaymentEventSubscription subscription)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await foreach (var paymentEvent in subscription.ReadAllAsync(timeout.Token))
            return paymentEvent;

        throw new InvalidOperationException("The subscription ended without an event");
    }

    private static Hash Hash(byte b)
    {
        var bytes = new byte[32];
        bytes[0] = b;
        return new Hash(bytes);
    }

    private static InvoiceSettledEvent Settled(byte b) =>
        new(Hash(b), LightningMoney.MilliSatoshis(1_000), s_now);
}