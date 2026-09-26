namespace NLightning.Domain.Tests.Channels.Validators;

using Domain.Channels.Constants;
using Domain.Channels.Validators;
using Domain.Channels.Validators.Parameters;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node;
using Domain.Node.Options;
using Domain.Protocol.Tlv;

public class ChannelOpenValidatorTests
{
    private static readonly LightningMoney s_feeRatePerKw = LightningMoney.Satoshis(10_000);
    private static readonly LightningMoney s_channelReserve = LightningMoney.Satoshis(1_000);

    private readonly ChannelOpenValidator _validator = new(new NodeOptions());

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

    [Theory]
    [InlineData(FeatureSupport.No)]
    [InlineData(FeatureSupport.Optional)]
    public void Given_FundingJustBelowLargeChannelAmount_When_PerformingMandatoryChecks_Then_DoesNotThrow(
        FeatureSupport largeChannels)
    {
        // Arrange
        // BOLT 2: without option_support_large_channel, funding_satoshis MUST be less than 2^24
        var fundingAmount = LightningMoney.Satoshis(16_777_215);
        var parameters = CreateParameters(fundingAmount, null, FeatureSupport.No, largeChannels);

        // Act
        var exception = Record.Exception(() => _validator.PerformMandatoryChecks(parameters, out _));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void Given_LargeFundingAndWumboNotNegotiated_When_PerformingMandatoryChecks_Then_Throws()
    {
        // Arrange
        var parameters = CreateParameters(ChannelConstants.LargeChannelAmount, null, FeatureSupport.No,
                                          FeatureSupport.No);

        // Act
        var exception = Assert.Throws<ChannelErrorException>(() => _validator.PerformMandatoryChecks(parameters,
                                                                    out _));

        // Assert
        Assert.Contains("large channels", exception.Message);
    }

    [Theory]
    [InlineData(FeatureSupport.Optional)]
    [InlineData(FeatureSupport.Compulsory)]
    public void Given_LargeFundingAndWumboNegotiated_When_PerformingMandatoryChecks_Then_DoesNotThrow(
        FeatureSupport largeChannels)
    {
        // Arrange
        var parameters = CreateParameters(LightningMoney.Satoshis(50_000_000), null, FeatureSupport.No,
                                          largeChannels);

        // Act
        var exception = Record.Exception(() => _validator.PerformMandatoryChecks(parameters, out _));

        // Assert
        Assert.Null(exception);
    }

    [Theory]
    [InlineData(40)] // zero_fee_commitments
    [InlineData(20)] // the retired option_anchor_outputs
    [InlineData(13)] // an odd bit is never part of a defined channel type
    public void Given_ChannelTypeWithUnsupportedBit_When_PerformingMandatoryChecks_Then_Throws(int bit)
    {
        // Arrange: BOLT 2: fail the channel if the channel_type is not suitable
        var channelType = FeatureSet.NewBasicChannelType();
        channelType.SetFeature(bit, true);
        var parameters = CreateParameters(LightningMoney.Satoshis(100_000), null, FeatureSupport.Optional);
        parameters = CopyWithChannelType(parameters, new ChannelTypeTlv(channelType));

        // Act
        var exception = Assert.Throws<ChannelErrorException>(() => _validator.PerformMandatoryChecks(parameters,
                                                                    out _));

        // Assert
        Assert.Contains("Unsupported channel type bit", exception.Message);
    }

    [Fact]
    public void Given_ChannelTypeWithScidAliasNotNegotiated_When_PerformingMandatoryChecks_Then_Throws()
    {
        // Arrange
        var channelType = FeatureSet.NewBasicChannelType();
        channelType.SetFeature(Feature.OptionScidAlias, true);
        var parameters = CopyWithChannelType(CreateParameters(LightningMoney.Satoshis(100_000), null,
                                                              FeatureSupport.No),
                                             new ChannelTypeTlv(channelType));

        // Act
        var exception = Assert.Throws<ChannelErrorException>(() => _validator.PerformMandatoryChecks(parameters,
                                                                    out _));

        // Assert
        Assert.Contains("Scid alias", exception.Message);
    }

    [Fact]
    public void Given_ChannelTypeWithScidAliasNegotiated_When_PerformingMandatoryChecks_Then_DoesNotThrow()
    {
        // Arrange
        var channelType = FeatureSet.NewBasicChannelType();
        channelType.SetFeature(Feature.OptionScidAlias, true);
        var parameters = CreateParameters(LightningMoney.Satoshis(100_000), null, FeatureSupport.No);
        parameters = new ChannelOpenMandatoryValidationParameters
        {
            ChannelTypeTlv = new ChannelTypeTlv(channelType),
            CurrentFeeRatePerKw = parameters.CurrentFeeRatePerKw,
            NegotiatedFeatures = new FeatureOptions { ScidAlias = FeatureSupport.Optional },
            FundingAmount = parameters.FundingAmount,
            ToSelfDelay = parameters.ToSelfDelay,
            MaxAcceptedHtlcs = parameters.MaxAcceptedHtlcs,
            DustLimitAmount = parameters.DustLimitAmount,
            ChannelReserveAmount = parameters.ChannelReserveAmount
        };

        // Act
        var exception = Record.Exception(() => _validator.PerformMandatoryChecks(parameters, out _));

        // Assert
        Assert.Null(exception);
    }

    private static ChannelOpenMandatoryValidationParameters CopyWithChannelType(
        ChannelOpenMandatoryValidationParameters parameters, ChannelTypeTlv channelTypeTlv)
    {
        return new ChannelOpenMandatoryValidationParameters
        {
            ChannelTypeTlv = channelTypeTlv,
            CurrentFeeRatePerKw = parameters.CurrentFeeRatePerKw,
            NegotiatedFeatures = parameters.NegotiatedFeatures,
            FundingAmount = parameters.FundingAmount,
            PushAmount = parameters.PushAmount,
            ToSelfDelay = parameters.ToSelfDelay,
            MaxAcceptedHtlcs = parameters.MaxAcceptedHtlcs,
            DustLimitAmount = parameters.DustLimitAmount,
            ChannelReserveAmount = parameters.ChannelReserveAmount
        };
    }

    [Fact]
    public void Given_BothInitialOutputsAtOrBelowReserve_When_PerformingMandatoryChecks_Then_Throws()
    {
        // Arrange - NL-220: 724 * 10000 / 1000 = 7240 sat fee; funding 9240, push 1000 leaves the funder 1000 sat.
        // Both outputs equal the 1000 sat reserve ("less than or equal" in BOLT 2).
        var parameters = CreateParameters(LightningMoney.Satoshis(9_240), LightningMoney.Satoshis(1_000),
                                          FeatureSupport.No);

        // Act
        var exception = Assert.Throws<ChannelErrorException>(() => _validator.PerformMandatoryChecks(parameters,
                                                                    out _));

        // Assert
        Assert.Contains("at or below the channel reserve", exception.Message);
    }

    [Fact]
    public void Given_FundeeOutputAboveReserve_When_PerformingMandatoryChecks_Then_DoesNotThrow()
    {
        // Arrange - funder output 999 sat (below the reserve) but the fundee output 1001 sat is above it
        var parameters = CreateParameters(LightningMoney.Satoshis(9_240), LightningMoney.Satoshis(1_001),
                                          FeatureSupport.No);

        // Act
        var exception = Record.Exception(() => _validator.PerformMandatoryChecks(parameters, out _));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void Given_AnchorsAndBothInitialOutputsBelowReserve_When_PerformingMandatoryChecks_Then_Throws()
    {
        // Arrange - 11240 sat fee + 660 sat anchors: funding 13_400, push 700 leaves the funder 800 sat
        var parameters = CreateParameters(LightningMoney.Satoshis(13_400), LightningMoney.Satoshis(700),
                                          FeatureSupport.Optional);

        // Act
        var exception = Assert.Throws<ChannelErrorException>(() => _validator.PerformMandatoryChecks(parameters,
                                                                    out _));

        // Assert
        Assert.Contains("at or below the channel reserve", exception.Message);
    }

    [Theory]
    [InlineData(253)]
    [InlineData(1_000)]
    [InlineData(2_500)]
    public void Given_PeerFeerateFarBelowOurEstimate_When_PerformingMandatoryChecks_Then_Accepted(long feeratePerKw)
    {
        // Arrange (NL-289: CLN opens at its own estimate, 253 sat/kw on an idle chain, while ours is 10,000)
        var parameters = WithFeeRate(CreateParameters(LightningMoney.Satoshis(500_000), null, FeatureSupport.No),
                                     LightningMoney.Satoshis(feeratePerKw));

        // Act
        var exception = Record.Exception(() => _validator.PerformMandatoryChecks(parameters, out _));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void Given_PeerFeerateBelowRelayFloor_When_PerformingMandatoryChecks_Then_Throws()
    {
        // Arrange (BOLT 2: too small for timely processing; BOLT 3's floor is 253 sat/kw)
        var parameters = WithFeeRate(CreateParameters(LightningMoney.Satoshis(500_000), null, FeatureSupport.No),
                                     LightningMoney.Satoshis(252));

        // Act
        var exception = Assert.Throws<ChannelErrorException>(() => _validator.PerformMandatoryChecks(parameters,
                                                                    out _));

        // Assert
        Assert.Contains("Fee rate per kw is too small", exception.Message);
    }

    [Fact]
    public void Given_PeerFeerateAboveMaximum_When_PerformingMandatoryChecks_Then_Throws()
    {
        // Arrange (BOLT 2: unreasonably large)
        var parameters = WithFeeRate(CreateParameters(LightningMoney.Satoshis(16_000_000), null, FeatureSupport.No),
                                     ChannelConstants.MaxFeePerKw + LightningMoney.Satoshis(1));

        // Act
        var exception = Assert.Throws<ChannelErrorException>(() => _validator.PerformMandatoryChecks(parameters,
                                                                    out _));

        // Assert
        Assert.Contains("Fee rate per kw is too large", exception.Message);
    }

    [Fact]
    public void Given_PeerFeerateAtMaximum_When_PerformingMandatoryChecks_Then_Accepted()
    {
        // Arrange: 100,000 sat/kw costs the funder 72,400 sat on a legacy commitment
        var parameters = WithFeeRate(CreateParameters(LightningMoney.Satoshis(1_000_000), null, FeatureSupport.No),
                                     ChannelConstants.MaxFeePerKw);

        // Act
        var exception = Record.Exception(() => _validator.PerformMandatoryChecks(parameters, out _));

        // Assert
        Assert.Null(exception);
    }

    private static ChannelOpenMandatoryValidationParameters WithFeeRate(
        ChannelOpenMandatoryValidationParameters parameters, LightningMoney feeRatePerKw)
    {
        return new ChannelOpenMandatoryValidationParameters
        {
            ChannelTypeTlv = parameters.ChannelTypeTlv,
            CurrentFeeRatePerKw = parameters.CurrentFeeRatePerKw,
            NegotiatedFeatures = parameters.NegotiatedFeatures,
            FundingAmount = parameters.FundingAmount,
            PushAmount = parameters.PushAmount,
            FeeRatePerKw = feeRatePerKw,
            ToSelfDelay = parameters.ToSelfDelay,
            MaxAcceptedHtlcs = parameters.MaxAcceptedHtlcs,
            DustLimitAmount = parameters.DustLimitAmount,
            ChannelReserveAmount = parameters.ChannelReserveAmount
        };
    }

    private static ChannelOpenMandatoryValidationParameters CreateParameters(
        LightningMoney fundingAmount, LightningMoney? pushAmount, FeatureSupport optionAnchors,
        FeatureSupport largeChannels = FeatureSupport.Optional)
    {
        return new ChannelOpenMandatoryValidationParameters
        {
            // option_static_remotekey (bit 12) compulsory
            ChannelTypeTlv = new ChannelTypeTlv([0x10, 0x00]),
            CurrentFeeRatePerKw = s_feeRatePerKw,
            NegotiatedFeatures = new FeatureOptions { OptionAnchors = optionAnchors, LargeChannels = largeChannels },
            FundingAmount = fundingAmount,
            PushAmount = pushAmount,
            ToSelfDelay = 144,
            MaxAcceptedHtlcs = 30,
            DustLimitAmount = LightningMoney.Satoshis(354),
            ChannelReserveAmount = s_channelReserve
        };
    }
}