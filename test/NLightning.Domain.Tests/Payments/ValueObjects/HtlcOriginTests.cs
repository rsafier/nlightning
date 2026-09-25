namespace NLightning.Domain.Tests.Payments.ValueObjects;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Payments.Enums;
using Domain.Payments.ValueObjects;

public class HtlcOriginTests
{
    [Fact]
    public void Given_LocalOrigin_When_Created_Then_OnlyPaymentHashSet()
    {
        // Arrange
        var hash = new Hash(Enumerable.Repeat((byte)1, 32).ToArray());

        // Act
        var origin = HtlcOrigin.Local(hash);

        // Assert
        Assert.Equal(HtlcOriginKind.Local, origin.Kind);
        Assert.Equal(hash, origin.PaymentHash);
        Assert.Null(origin.IncomingChannelId);
        Assert.Null(origin.IncomingHtlcId);
    }

    [Fact]
    public void Given_ForwardedOrigin_When_Created_Then_IncomingHtlcSetAndEqualByValue()
    {
        // Arrange
        var channelId = new ChannelId(Enumerable.Repeat((byte)2, 32).ToArray());

        // Act
        var origin = HtlcOrigin.Forwarded(channelId, 5);

        // Assert
        Assert.Equal(HtlcOriginKind.Forwarded, origin.Kind);
        Assert.Null(origin.PaymentHash);
        Assert.Equal(channelId, origin.IncomingChannelId);
        Assert.Equal(5UL, origin.IncomingHtlcId);
        Assert.Equal(HtlcOrigin.Forwarded(channelId, 5), origin);
    }
}