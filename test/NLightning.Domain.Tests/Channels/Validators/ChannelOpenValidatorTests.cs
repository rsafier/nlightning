namespace NLightning.Domain.Tests.Channels.Validators;

using Domain.Channels.Constants;
using Domain.Channels.Validators;
using Domain.Channels.Validators.Parameters;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;

public class ChannelOpenValidatorTests
{
    [Fact]
    public void Given_NoChannelTypeTlv_When_PerformMandatoryChecks_Then_ThrowsChannelErrorException()
    {
        // Arrange (BOLT 2: a missing channel_type fails the channel)
        var validator = new ChannelOpenValidator(new NodeOptions());
        var parameters = new ChannelOpenMandatoryValidationParameters
        {
            ChannelTypeTlv = null,
            CurrentFeeRatePerKw = LightningMoney.Satoshis(1),
            NegotiatedFeatures = new FeatureOptions(),
            DustLimitAmount = ChannelConstants.MinDustLimitAmount,
            ChannelReserveAmount = ChannelConstants.MinDustLimitAmount
        };

        // Act
        var exception =
            Assert.Throws<ChannelErrorException>(() => validator.PerformMandatoryChecks(parameters, out _));

        // Assert
        Assert.Contains("ChannelTypeTlv", exception.Message);
    }
}