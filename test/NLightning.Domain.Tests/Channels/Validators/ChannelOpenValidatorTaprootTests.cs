namespace NLightning.Domain.Tests.Channels.Validators;

using Domain.Channels.Validators;
using Domain.Channels.Validators.Parameters;
using Domain.Channels.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node;
using Domain.Node.Options;
using Domain.Protocol.Tlv;

/// <summary>
/// The simple taproot <c>channel_type</c> in the open checks (taproot wave t02 lane V2, used by the dual-funded open):
/// bit 80 without 12/22 is known when <c>option_simple_taproot</c> is negotiated and never announced.
/// </summary>
public class ChannelOpenValidatorTaprootTests
{
    private readonly ChannelOpenValidator _validator = new(new NodeOptions());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Given_ATaprootChannelTypeAndTheOptionNegotiated_When_Checked_Then_Accepted(bool withScidAlias)
    {
        // Arrange: {80} or {80, 46}, as LND 0.21 accepts them
        var channelType = TaprootType();
        if (withScidAlias)
            channelType.SetFeature((int)Feature.OptionScidAlias - 1, true);
        var parameters = Parameters(channelType, FeatureSupport.Optional, announce: false);

        // Act
        var exception = Record.Exception(() => _validator.PerformMandatoryChecks(parameters, out _));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void Given_ATaprootChannelTypeWithoutTheOption_When_Checked_Then_Refused()
    {
        // Arrange
        var parameters = Parameters(TaprootType(), FeatureSupport.No, announce: false);

        // Act
        var exception = Assert.Throws<ChannelErrorException>(() => _validator.PerformMandatoryChecks(parameters,
                                                                    out _));

        // Assert
        Assert.Contains("option_simple_taproot", exception.Message);
    }

    [Fact]
    public void Given_APublicTaprootChannel_When_Checked_Then_Refused()
    {
        // Arrange: bolt-simple-taproot.md, a taproot channel MUST NOT set announce_channel
        var parameters = Parameters(TaprootType(), FeatureSupport.Optional, announce: true);

        // Act
        var exception = Assert.Throws<ChannelErrorException>(() => _validator.PerformMandatoryChecks(parameters,
                                                                    out _));

        // Assert
        Assert.Contains("announced", exception.Message);
    }

    [Fact]
    public void Given_Bit80WithStaticRemoteKey_When_Checked_Then_TheBitIsUnsupported()
    {
        // Arrange: not a taproot type (12 set), so bit 80 is an unknown bit
        var channelType = TaprootType();
        channelType.SetFeature((int)Feature.OptionStaticRemoteKey - 1, true);
        var parameters = Parameters(channelType, FeatureSupport.Optional, announce: false);

        // Act
        var exception = Assert.Throws<ChannelErrorException>(() => _validator.PerformMandatoryChecks(parameters,
                                                                    out _));

        // Assert
        Assert.Contains("Unsupported channel type bit 80", exception.Message);
    }

    private static FeatureSet TaprootType()
    {
        var channelType = FeatureSet.DeserializeFromBytes([]);
        channelType.SetFeature(TaprootChannelType.CompulsoryBit, true);
        return channelType;
    }

    private static ChannelOpenMandatoryValidationParameters Parameters(FeatureSet channelType,
                                                                      FeatureSupport simpleTaproot, bool announce) =>
        new()
        {
            ChannelTypeTlv = new ChannelTypeTlv(channelType),
            CurrentFeeRatePerKw = LightningMoney.Satoshis(2_500),
            NegotiatedFeatures = new FeatureOptions
            {
                OptionSimpleTaproot = simpleTaproot,
                ScidAlias = FeatureSupport.Optional,
                AllowExperimentalFeatures = true
            },
            FundingAmount = LightningMoney.Satoshis(1_000_000),
            ToSelfDelay = 144,
            MaxAcceptedHtlcs = 30,
            DustLimitAmount = LightningMoney.Satoshis(354),
            ChannelReserveAmount = LightningMoney.Satoshis(10_000),
            ChannelFlags = new ChannelFlags(announce ? ChannelFlag.AnnounceChannel : ChannelFlag.None)
        };
}