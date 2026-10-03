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
/// The simple taproot channel type in the open validator (NL-877 T5, bolt-simple-taproot.md §open_channel): bit 80
/// alone (plus scid_alias/zeroconf), in the v1 and the dual-funded flow, only with option_simple_taproot and
/// option_simple_close negotiated, and never for a public channel.
/// </summary>
public class ChannelOpenValidatorTaprootTests
{
    private readonly ChannelOpenValidator _validator = new(new NodeOptions());

    private static FeatureOptions Negotiated => new()
    {
        OptionSimpleTaproot = FeatureSupport.Optional,
        OptionSimpleClose = FeatureSupport.Optional,
        ScidAlias = FeatureSupport.Optional,
        AllowExperimentalFeatures = true
    };

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Given_TaprootChannelTypeNegotiated_When_Checked_Then_Accepted(bool scidAlias, bool zeroConf)
    {
        // Arrange - LND 0.21 sends exactly {80}, {80, 46}, {80, 50} or {80, 46, 50}
        var channelType = TaprootType();
        if (scidAlias)
            channelType.SetFeature(Feature.OptionScidAlias, true);
        if (zeroConf)
            channelType.SetFeature((int)Feature.OptionZeroconf - 1, true);
        var validator = zeroConf
                            ? new ChannelOpenValidator(new NodeOptions
                            {
                                Features = new FeatureOptions { ZeroConf = FeatureSupport.Optional }
                            })
                            : _validator;

        // Act
        var exception = Record.Exception(() => validator.PerformMandatoryChecks(Parameters(channelType), out _));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void Given_TaprootTypeWithAnchorsBits_When_Checked_Then_Refused()
    {
        // Arrange - {80, 12, 22} is not a taproot type (LND: errUnsupportedChannelType)
        var channelType = FeatureSet.NewBasicChannelType();
        channelType.SetFeature(Feature.OptionAnchors, true);
        channelType.SetFeature(TaprootChannelType.CompulsoryBit, true);

        // Act / Assert
        var exception = Assert.Throws<ChannelErrorException>(
            () => _validator.PerformMandatoryChecks(Parameters(channelType), out _));
        Assert.Contains("option_static_remotekey", exception.Message);
    }

    [Fact]
    public void Given_TaprootTypeNotNegotiated_When_Checked_Then_Refused()
    {
        var parameters = Parameters(TaprootType(), new FeatureOptions { OptionSimpleClose = FeatureSupport.Optional });

        var exception = Assert.Throws<ChannelErrorException>(() => _validator.PerformMandatoryChecks(parameters,
                                                                    out _));
        Assert.Contains("option_simple_taproot is not negotiated", exception.Message);
    }

    [Fact]
    public void Given_TaprootTypeWithoutSimpleClose_When_Checked_Then_Refused()
    {
        var parameters = Parameters(TaprootType(), new FeatureOptions
        {
            OptionSimpleTaproot = FeatureSupport.Optional,
            OptionSimpleClose = FeatureSupport.No,
            AllowExperimentalFeatures = true
        });

        var exception = Assert.Throws<ChannelErrorException>(() => _validator.PerformMandatoryChecks(parameters,
                                                                    out _));
        Assert.Contains("option_simple_close", exception.Message);
    }

    [Fact]
    public void Given_PublicTaprootOpen_When_Checked_Then_Refused()
    {
        // The spec: the opener MUST NOT set announce_channel (LND: "taproot channel type for public channel")
        var parameters = Parameters(TaprootType(), announce: true);

        var exception = Assert.Throws<ChannelErrorException>(() => _validator.PerformMandatoryChecks(parameters,
                                                                    out _));
        Assert.Contains("public", exception.Message);
    }

    [Fact]
    public void Given_TaprootTypeInAFlowThatCannotRunIt_When_Checked_Then_Refused()
    {
        // A flow that cannot sign MuSig2 commitments leaves AllowSimpleTaproot false
        var parameters = Parameters(TaprootType(), allowTaproot: false);

        var exception = Assert.Throws<ChannelErrorException>(() => _validator.PerformMandatoryChecks(parameters,
                                                                    out _));
        Assert.Contains("not supported in this open flow", exception.Message);
    }

    [Fact]
    public void Given_TaprootChannelParams_When_ToChannelType_Then_Bit80AloneOrWithScidAlias()
    {
        // Arrange
        var party = new ChannelParty(LightningMoney.Satoshis(354), LightningMoney.Satoshis(10_000),
                                     LightningMoney.MilliSatoshis(1_000), 30, LightningMoney.Satoshis(1_000_000), 144);
        var plain = new ChannelParams(party, party, LightningMoney.Satoshis(2_500), 3, true, FeatureSupport.No)
        {
            OptionSimpleTaproot = true
        };
        var aliased = new ChannelParams(party, party, LightningMoney.Satoshis(2_500), 3, true,
                                        FeatureSupport.Compulsory)
        {
            OptionSimpleTaproot = true
        };

        // Act / Assert - the bytes LND compares: 11 bytes, big-endian, 0x01 then zeros for {80}
        Assert.Equal([80], plain.ToChannelType().GetSetBits());
        Assert.Equal([0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], plain.ToChannelType().GetWireBytes());
        Assert.Equal([46, 80], aliased.ToChannelType().GetSetBits());
    }

    [Fact]
    public void Given_Bit80WithStaticRemoteKeyOnly_When_Checked_Then_Refused()
    {
        // Arrange - {80, 12} is not a taproot type either
        var channelType = TaprootType();
        channelType.SetFeature(Feature.OptionStaticRemoteKey, true);

        // Act
        var exception = Assert.Throws<ChannelErrorException>(
            () => _validator.PerformMandatoryChecks(Parameters(channelType), out _));

        // Assert
        Assert.Contains("option_static_remotekey", exception.Message);
    }

    private static FeatureSet TaprootType()
    {
        var channelType = FeatureSet.DeserializeFromBytes([]);
        channelType.SetFeature(TaprootChannelType.CompulsoryBit, true);
        return channelType;
    }

    private static ChannelOpenMandatoryValidationParameters Parameters(FeatureSet channelType,
                                                                       FeatureOptions? negotiated = null,
                                                                       bool announce = false, bool allowTaproot = true)
    {
        return new ChannelOpenMandatoryValidationParameters
        {
            ChannelTypeTlv = new ChannelTypeTlv(channelType),
            CurrentFeeRatePerKw = LightningMoney.Satoshis(2_500),
            NegotiatedFeatures = negotiated ?? Negotiated,
            FundingAmount = LightningMoney.Satoshis(1_000_000),
            ToSelfDelay = 144,
            MaxAcceptedHtlcs = 30,
            FeeRatePerKw = LightningMoney.Satoshis(2_500),
            DustLimitAmount = LightningMoney.Satoshis(354),
            ChannelReserveAmount = LightningMoney.Satoshis(10_000),
            ChannelFlags = new ChannelFlags(announce ? ChannelFlag.AnnounceChannel : ChannelFlag.None),
            AllowSimpleTaproot = allowTaproot
        };
    }
}