namespace NLightning.Domain.Tests.Channels.Policies;

using Domain.Channels.Policies;
using Domain.Money;
using Domain.Node.Options;

public class MaxHtlcValueInFlightRulesTests
{
    [Fact]
    public void Given_SpliceNegotiated_When_Announced_Then_NoCap()
    {
        // Arrange: a 140,000 sat channel that a splice may grow (NL-880)
        var options = new NodeOptions { AllowUpToPercentageOfChannelFundsInFlight = 80 };

        // Act
        var announced = MaxHtlcValueInFlightRules.GetAnnounced(options, LightningMoney.Satoshis(140_000), true);

        // Assert
        Assert.Equal(ulong.MaxValue, announced.MilliSatoshi);
        Assert.Equal(MaxHtlcValueInFlightRules.NoLimit, announced);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Given_NoSpliceOrTheLimitKept_When_Announced_Then_AShareOfTheCapacity(bool spliceNegotiated,
                                                                                  bool limitSpliceable)
    {
        // Arrange
        var options = new NodeOptions
        {
            AllowUpToPercentageOfChannelFundsInFlight = 80,
            LimitInFlightOnSpliceableChannels = limitSpliceable
        };

        // Act
        var announced = MaxHtlcValueInFlightRules.GetAnnounced(options, LightningMoney.Satoshis(140_000),
                                                               spliceNegotiated);

        // Assert: 80 % of 140,000 sat
        Assert.Equal(LightningMoney.Satoshis(112_000), announced);
    }
}