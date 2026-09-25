namespace NLightning.Domain.Tests.Channels.Validators;

using Domain.Channels.Validators;
using Domain.Channels.Validators.Parameters;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.Tlv;

public class ChannelOpenValidatorTests
{
    private static readonly LightningMoney s_feeRatePerKw = LightningMoney.Satoshis(10_000);
    private static readonly LightningMoney s_channelReserve = LightningMoney.Satoshis(1_000);

    private readonly ChannelOpenValidator _validator = new(new NodeOptions());

    [Fact]
    public void Given_PushAmountEqualToFundingAmount_When_PerformingMandatoryChecks_Then_ThrowsFunderCannotPayFee()
    {
        // Arrange
        var fundingAmount = LightningMoney.Satoshis(100_000);
        var parameters = CreateParameters(fundingAmount, LightningMoney.Satoshis(100_000), FeatureSupport.No);

        // Act
        var exception = Assert.Throws<ChannelErrorException>(() => _validator.PerformMandatoryChecks(parameters,
                                                                    out _));

        // Assert
        Assert.Contains("Funder amount is too small to cover fees", exception.Message);
    }

    [Fact]
    public void Given_FunderAmountEqualToFee_When_PerformingMandatoryChecks_Then_DoesNotThrow()
    {
        // Arrange
        // 724 * 10000 / 1000 = 7240 sat fee left to the funder
        var fundingAmount = LightningMoney.Satoshis(100_000);
        var parameters = CreateParameters(fundingAmount, LightningMoney.Satoshis(92_760), FeatureSupport.No);

        // Act
        var exception = Record.Exception(() => _validator.PerformMandatoryChecks(parameters, out _));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void Given_AnchorsAndFunderAmountBelowFeePlusAnchors_When_PerformingMandatoryChecks_Then_Throws()
    {
        // Arrange
        // 1124 * 10000 / 1000 = 11240 sat fee + 2 * 330 sat anchors = 11900 sat
        var fundingAmount = LightningMoney.Satoshis(100_000);
        var parameters = CreateParameters(fundingAmount, LightningMoney.Satoshis(88_101), FeatureSupport.Optional);

        // Act
        var exception = Assert.Throws<ChannelErrorException>(() => _validator.PerformMandatoryChecks(parameters,
                                                                    out _));

        // Assert
        Assert.Contains("Funder amount is too small to cover fees", exception.Message);
    }

    [Fact]
    public void Given_PeerFeeRateHigherThanOurs_When_PerformingMandatoryChecks_Then_UsesPeerFeeRateForFeeCheck()
    {
        // Arrange
        // At our 10000 sat/kw the funder's 7240 sat would cover 724 weight, but the peer's 20000 sat/kw needs 14480
        var fundingAmount = LightningMoney.Satoshis(100_000);
        var parameters = CreateParameters(fundingAmount, LightningMoney.Satoshis(92_760), FeatureSupport.No);
        parameters = new ChannelOpenMandatoryValidationParameters
        {
            ChannelTypeTlv = parameters.ChannelTypeTlv,
            CurrentFeeRatePerKw = parameters.CurrentFeeRatePerKw,
            NegotiatedFeatures = parameters.NegotiatedFeatures,
            FundingAmount = parameters.FundingAmount,
            PushAmount = parameters.PushAmount,
            FeeRatePerKw = LightningMoney.Satoshis(20_000),
            ToSelfDelay = parameters.ToSelfDelay,
            MaxAcceptedHtlcs = parameters.MaxAcceptedHtlcs,
            DustLimitAmount = parameters.DustLimitAmount,
            ChannelReserveAmount = parameters.ChannelReserveAmount
        };

        // Act
        var exception = Assert.Throws<ChannelErrorException>(() => _validator.PerformMandatoryChecks(parameters,
                                                                    out _));

        // Assert
        Assert.Contains("Funder amount is too small to cover fees", exception.Message);
    }

    [Fact]
    public void Given_PushAmountOneMsatAboveFundingAmount_When_PerformingMandatoryChecks_Then_Throws()
    {
        // Arrange
        var fundingAmount = LightningMoney.Satoshis(100_000);
        var pushAmount = LightningMoney.MilliSatoshis(100_000_001UL);
        var parameters = CreateParameters(fundingAmount, pushAmount, FeatureSupport.No);

        // Act
        var exception = Assert.Throws<ChannelErrorException>(() => _validator.PerformMandatoryChecks(parameters,
                                                                    out _));

        // Assert
        Assert.Contains("Push amount is too large", exception.Message);
    }

    [Fact]
    public void Given_AnchorsAndFundingBelowAnchorFeePlusReserve_When_PerformingMandatoryChecks_Then_Throws()
    {
        // Arrange
        // 1124 * 10000 / 1000 = 11240 sat fee + 2 * 330 sat anchors + 1000 sat reserve = 12900 sat
        var parameters = CreateParameters(LightningMoney.Satoshis(12_899), null, FeatureSupport.Optional);

        // Act
        var exception = Assert.Throws<ChannelErrorException>(() => _validator.PerformMandatoryChecks(parameters,
                                                                    out _));

        // Assert
        Assert.Contains("too small to cover fees", exception.Message);
    }

    [Fact]
    public void Given_AnchorsAndFundingEqualToAnchorFeePlusReserve_When_PerformingMandatoryChecks_Then_DoesNotThrow()
    {
        // Arrange
        var parameters = CreateParameters(LightningMoney.Satoshis(12_900), null, FeatureSupport.Optional);

        // Act
        var exception = Record.Exception(() => _validator.PerformMandatoryChecks(parameters, out _));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void Given_NoAnchorsAndFundingCoveringNoAnchorFee_When_PerformingMandatoryChecks_Then_DoesNotThrow()
    {
        // Arrange
        // 724 * 10000 / 1000 = 7240 sat fee + 1000 sat reserve = 8240 sat
        var parameters = CreateParameters(LightningMoney.Satoshis(8_240), null, FeatureSupport.No);

        // Act
        var exception = Record.Exception(() => _validator.PerformMandatoryChecks(parameters, out _));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void Given_NoAnchorsAndFundingBelowNoAnchorFeePlusReserve_When_PerformingMandatoryChecks_Then_Throws()
    {
        // Arrange
        var parameters = CreateParameters(LightningMoney.Satoshis(8_239), null, FeatureSupport.No);

        // Act
        var exception = Assert.Throws<ChannelErrorException>(() => _validator.PerformMandatoryChecks(parameters,
                                                                    out _));

        // Assert
        Assert.Contains("too small to cover fees", exception.Message);
    }

    private static ChannelOpenMandatoryValidationParameters CreateParameters(
        LightningMoney fundingAmount, LightningMoney? pushAmount, FeatureSupport optionAnchors)
    {
        return new ChannelOpenMandatoryValidationParameters
        {
            // option_static_remotekey (bit 12) compulsory
            ChannelTypeTlv = new ChannelTypeTlv([0x10, 0x00]),
            CurrentFeeRatePerKw = s_feeRatePerKw,
            NegotiatedFeatures = new FeatureOptions { OptionAnchors = optionAnchors },
            FundingAmount = fundingAmount,
            PushAmount = pushAmount,
            ToSelfDelay = 144,
            MaxAcceptedHtlcs = 30,
            DustLimitAmount = LightningMoney.Satoshis(354),
            ChannelReserveAmount = s_channelReserve
        };
    }
}