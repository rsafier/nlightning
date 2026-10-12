namespace NLightning.Application.Tests.Payments.Send;

using Application.Payments.Send;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Models;

/// <summary>
/// NL-326: a fulfill's verified hold times are written by index onto the payment's stored route, so they are recorded
/// only when the stored route is the fulfilled part's (<see cref="PaymentService.IsSameRoute"/>); another MPP part or
/// round would pair them with the wrong nodes.
/// </summary>
public class PaymentFulfillHoldTimeRouteTests
{
    private static readonly CompactPubKey s_carol = new TestNodeKeyManager(0x03).NodeId;
    private static readonly CompactPubKey s_david = new TestNodeKeyManager(0x04).NodeId;

    [Fact]
    public void Given_TheSameHopsAndSecrets_When_Compared_Then_TheRoutesMatchWhateverTheHoldTimes()
    {
        // Arrange
        var stored = Route(0x11, 0x12, new ShortChannelId(300, 1, 0));
        var part = Route(0x11, 0x12, new ShortChannelId(300, 1, 0));
        stored[0] = stored[0] with { HoldTime = TimeSpan.FromMilliseconds(300) };

        // Act
        var same = PaymentService.IsSameRoute(stored, part);

        // Assert
        Assert.True(same);
    }

    [Fact]
    public void Given_AnotherPartsSecrets_When_Compared_Then_TheRoutesDiffer()
    {
        // Arrange: the same path, another onion (another MPP part or round)
        var stored = Route(0x11, 0x12, new ShortChannelId(300, 1, 0));
        var part = Route(0x21, 0x22, new ShortChannelId(300, 1, 0));

        // Act
        var same = PaymentService.IsSameRoute(stored, part);

        // Assert
        Assert.False(same);
    }

    [Fact]
    public void Given_AnotherFirstChannel_When_Compared_Then_TheRoutesDiffer()
    {
        // Arrange
        var stored = Route(0x11, 0x12, new ShortChannelId(300, 1, 0));
        var part = Route(0x11, 0x12, new ShortChannelId(301, 1, 0));

        // Act
        var same = PaymentService.IsSameRoute(stored, part);

        // Assert
        Assert.False(same);
    }

    [Fact]
    public void Given_ADifferentLength_When_Compared_Then_TheRoutesDiffer()
    {
        // Arrange
        var stored = Route(0x11, 0x12, new ShortChannelId(300, 1, 0));
        var part = stored.Take(1).ToList();

        // Act
        var same = PaymentService.IsSameRoute(stored, part);

        // Assert
        Assert.False(same);
    }

    private static List<PaymentHop> Route(byte secret1, byte secret2, ShortChannelId firstScid) =>
    [
        new(s_carol, firstScid, LightningMoney.MilliSatoshis(10_010), 500, Secret(secret1)),
        new(s_david, new ShortChannelId(101, 2, 1), LightningMoney.MilliSatoshis(10_000), 480, Secret(secret2))
    ];

    private static Secret Secret(byte tag) => new(Enumerable.Repeat(tag, 32).ToArray());
}