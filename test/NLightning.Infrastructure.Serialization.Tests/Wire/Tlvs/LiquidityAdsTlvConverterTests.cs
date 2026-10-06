namespace NLightning.Infrastructure.Serialization.Tests.Wire.Tlvs;

using Domain.LiquidityAds.Enums;
using Domain.LiquidityAds.Models;
using Domain.Protocol.Tlv;
using NLightning.Infrastructure.Serialization.Wire;

/// <summary>
/// The three converters of the liquidity ads TLV 1339 (NL-850): one per meaning, each strict on its own value.
/// </summary>
public class LiquidityAdsTlvConverterTests
{
    private const ulong LiquidityAdsType = 1339;

    private static readonly FundingRate s_rate = new(25_000, 250_000, 750, 150, 50, 500);

    // Eclair's tx_init_rbf / tx_ack_rbf records (LightningMessageCodecsSpec)
    private static readonly byte[] s_requestValue =
        Convert.FromHexString("000000000000C350000061A80003D09002EE009600000032000001F40000");

    private static readonly byte[] s_willFundValue =
        Convert.FromHexString("000061A80003D09002EE009600000032000001F40004DEADBEEF" + new string('0', 128));

    private static readonly byte[] s_ratesValue =
        Convert.FromHexString("0001000186A00007A1200226006400001388000003E8000101");

    [Fact]
    public void Given_RequestFunding_When_ConvertedBothWays_Then_RoundTrips()
    {
        // Arrange
        var converter = new WireRegistry().GetTlvDefinition<RequestFundingTlv>()!;
        var expected = new RequestFundingTlv(new RequestFunding(50_000, s_rate,
                                                                LiquidityPaymentDetails.FromChannelBalance));

        // Act
        var baseTlv = converter.Encode(expected);
        var tlv = converter.Decode(new BaseTlv(LiquidityAdsType, s_requestValue));

        // Assert
        Assert.Equal(s_requestValue, baseTlv.Value);
        Assert.Equal(expected, tlv);
    }

    [Fact]
    public void Given_ProvideFunding_When_ConvertedBothWays_Then_RoundTrips()
    {
        // Arrange
        var converter = new WireRegistry().GetTlvDefinition<ProvideFundingTlv>()!;

        // Act
        var tlv = converter.Decode(new BaseTlv(LiquidityAdsType, s_willFundValue));
        var baseTlv = converter.Encode(tlv);

        // Assert
        Assert.Equal(s_rate, tlv.WillFund.Rate);
        Assert.Equal(s_willFundValue, baseTlv.Value);
    }

    [Fact]
    public void Given_WillFundRates_When_ConvertedBothWays_Then_RoundTrips()
    {
        // Arrange
        var converter = new WireRegistry().GetTlvDefinition<WillFundRatesTlv>()!;

        // Act
        var tlv = converter.Decode(new BaseTlv(LiquidityAdsType, s_ratesValue));
        var baseTlv = converter.Encode(tlv);

        // Assert
        Assert.Single(tlv.Rates.Rates);
        Assert.True(tlv.Rates.Supports(LiquidityPaymentType.FromChannelBalance));
        Assert.Equal(s_ratesValue, baseTlv.Value);
    }

    [Fact]
    public void Given_AnotherType_When_Converted_Then_EachConverterRefusesIt()
    {
        // Act & Assert
        Assert.Throws<InvalidCastException>(() => new WireRegistry().GetTlvDefinition<RequestFundingTlv>()!
                                                .Decode(new BaseTlv(1337, s_requestValue)));
        Assert.Throws<InvalidCastException>(() => new WireRegistry().GetTlvDefinition<ProvideFundingTlv>()!
                                                .Decode(new BaseTlv(1337, s_willFundValue)));
        Assert.Throws<InvalidCastException>(() => new WireRegistry().GetTlvDefinition<WillFundRatesTlv>()!
                                                .Decode(new BaseTlv(1337, s_ratesValue)));
    }

    [Fact]
    public void Given_TheOtherMeaningsValue_When_Converted_Then_ItIsRefused()
    {
        // Act & Assert: a record that belongs to another message never decodes as this one
        Assert.Throws<InvalidCastException>(() => new WireRegistry().GetTlvDefinition<RequestFundingTlv>()!
                                                .Decode(new BaseTlv(LiquidityAdsType, s_willFundValue)));
        Assert.Throws<InvalidCastException>(() => new WireRegistry().GetTlvDefinition<ProvideFundingTlv>()!
                                                .Decode(new BaseTlv(LiquidityAdsType, s_requestValue)));
        Assert.Throws<InvalidCastException>(() => new WireRegistry().GetTlvDefinition<WillFundRatesTlv>()!
                                                .Decode(new BaseTlv(LiquidityAdsType, s_requestValue)));
    }

    [Fact]
    public void Given_TrailingByte_When_Converted_Then_ItIsRefused()
    {
        // Act & Assert
        Assert.Throws<InvalidCastException>(() => new WireRegistry().GetTlvDefinition<RequestFundingTlv>()!
                                                .Decode(new BaseTlv(LiquidityAdsType, [.. s_requestValue, 0])));
        Assert.Throws<InvalidCastException>(() => new WireRegistry().GetTlvDefinition<ProvideFundingTlv>()!
                                                .Decode(new BaseTlv(LiquidityAdsType, [.. s_willFundValue, 0])));
        Assert.Throws<InvalidCastException>(() => new WireRegistry().GetTlvDefinition<WillFundRatesTlv>()!
                                                .Decode(new BaseTlv(LiquidityAdsType, [.. s_ratesValue, 0])));
    }
}