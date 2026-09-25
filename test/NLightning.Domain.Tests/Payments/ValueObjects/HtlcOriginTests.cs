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

    [Fact]
    public void Given_DefaultOrigin_When_Checked_Then_Invalid()
    {
        // Arrange
        var origin = default(HtlcOrigin);

        // Act & Assert
        Assert.False(origin.IsValid);
        Assert.False(Enum.IsDefined(origin.Kind));
    }

    [Fact]
    public void Given_FactoryOrigins_When_Checked_Then_Valid()
    {
        // Arrange
        var local = HtlcOrigin.Local(new Hash(new byte[32]));
        var forwarded = HtlcOrigin.Forwarded(new ChannelId(new byte[32]), 0);

        // Act & Assert
        Assert.True(local.IsValid);
        Assert.True(forwarded.IsValid);
    }

    [Fact]
    public void Given_OriginKinds_When_Read_Then_PersistedValuesAreStable()
    {
        // Assert (0 is reserved for an unset origin)
        Assert.Equal(1, (byte)HtlcOriginKind.Local);
        Assert.Equal(2, (byte)HtlcOriginKind.Forwarded);
    }
}