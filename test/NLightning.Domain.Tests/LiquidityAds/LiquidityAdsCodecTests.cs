using NLightning.Tests.Utils.Vectors;

namespace NLightning.Domain.Tests.LiquidityAds;

using Domain.LiquidityAds;
using Domain.LiquidityAds.Enums;
using Domain.LiquidityAds.Models;

public class LiquidityAdsCodecTests
{
    private static readonly FundingRate s_rate1 = new(100_000, 500_000, 550, 100, 5_000, 1_000);
    private static readonly FundingRate s_rate2 = new(500_000, 5_000_000, 1_100, 75, 0, 1_500);

    [Fact]
    public void Given_OneRateAndChannelBalance_When_Encoded_Then_EqualsEclairsInitTlv()
    {
        // Arrange
        var rates = WillFundRates.Create([s_rate1], [LiquidityPaymentType.FromChannelBalance]);

        // Act
        var encoded = LiquidityAdsCodec.EncodeWillFundRates(rates);

        // Assert
        Assert.Equal(LiquidityAdsEclairVectors.InitRatesOne, Convert.ToHexStringLower(encoded));
    }

    [Fact]
    public void Given_EclairsTwoRatesWithFivePaymentTypes_When_Decoded_Then_EveryTypeIsSupportedAndItReencodes()
    {
        // Arrange
        var bytes = Convert.FromHexString(LiquidityAdsEclairVectors.InitRatesTwo);

        // Act
        var decoded = LiquidityAdsCodec.TryDecodeWillFundRates(bytes, out var rates);

        // Assert
        Assert.True(decoded);
        Assert.Equal([s_rate1, s_rate2], rates!.Rates);
        Assert.True(rates.Supports(LiquidityPaymentType.FromChannelBalance));
        Assert.True(rates.Supports(LiquidityPaymentType.FromFutureHtlc));
        Assert.True(rates.Supports(LiquidityPaymentType.FromFutureHtlcWithPreimage));
        Assert.True(rates.Supports(LiquidityPaymentType.FromChannelBalanceForFutureHtlc));
        Assert.True(rates.SupportsBit(211));
        Assert.False(rates.SupportsBit(1));
        Assert.Equal(bytes, LiquidityAdsCodec.EncodeWillFundRates(rates));
        Assert.Equal(bytes, LiquidityAdsCodec.EncodeWillFundRates(WillFundRates.Create(
                                                                       [s_rate1, s_rate2],
                                                                       [
                                                                           .. Enum.GetValues<LiquidityPaymentType>(),
                                                                           (LiquidityPaymentType)211
                                                                       ])));
    }

    [Fact]
    public void Given_NodeAnnouncementRates_When_RoundTripped_Then_ByteExact()
    {
        // Arrange
        var rates = WillFundRates.Create([s_rate1, s_rate2], [LiquidityPaymentType.FromChannelBalance]);

        // Act
        var encoded = LiquidityAdsCodec.EncodeWillFundRates(rates);

        // Assert
        Assert.Equal(LiquidityAdsEclairVectors.NodeAnnouncementRates, Convert.ToHexStringLower(encoded));
    }

    [Fact]
    public void Given_UnknownPaymentTypes_When_Decoded_Then_TheyAreKeptAndReencodedAsReceived()
    {
        // Arrange
        var bytes = Convert.FromHexString(LiquidityAdsEclairVectors.RatesWithUnknownTypes);

        // Act
        var decoded = LiquidityAdsCodec.TryDecodeWillFundRates(bytes, out var rates);

        // Assert
        Assert.True(decoded);
        Assert.True(rates!.SupportsBit(0));
        Assert.True(rates.SupportsBit(75));
        Assert.True(rates.SupportsBit(211));
        Assert.False(rates.SupportsBit(128));
        Assert.Equal(bytes, LiquidityAdsCodec.EncodeWillFundRates(rates));
    }

    [Fact]
    public void Given_NoRatesAndAnEmptyBitfield_When_Decoded_Then_Valid()
    {
        // Act
        var decoded = LiquidityAdsCodec.TryDecodeWillFundRates(Convert.FromHexString("00000000"), out var rates);

        // Assert
        Assert.True(decoded);
        Assert.Empty(rates!.Rates);
        Assert.Empty(rates.EncodedPaymentTypes);
    }

    [Theory]
    [InlineData(LiquidityAdsEclairVectors.TxInitRbfRequest, 50_000UL, 25_000U, 250_000U, (ushort)750, (ushort)150, 50U,
                500U)]
    [InlineData(LiquidityAdsEclairVectors.SpliceInitRequest, 100_000UL, 100_000U, 100_000U, (ushort)400, (ushort)150, 0U,
                0U)]
    [InlineData(LiquidityAdsEclairVectors.OpenRequest, 750_000UL, 500_000U, 5_000_000U, (ushort)1_100, (ushort)75, 0U,
                1_500U)]
    public void Given_EclairsRequests_When_DecodedAndReencoded_Then_ByteExact(string hex, ulong requested, uint min,
                                                                             uint max, ushort weight, ushort basis,
                                                                             uint feeBase, uint creation)
    {
        // Arrange
        var bytes = Convert.FromHexString(hex);

        // Act
        var decoded = LiquidityAdsCodec.TryDecodeRequestFunding(bytes, out var request);

        // Assert
        Assert.True(decoded);
        Assert.Equal(requested, request!.RequestedSat);
        Assert.Equal(new FundingRate(min, max, weight, basis, feeBase, creation), request.Rate);
        Assert.Same(LiquidityPaymentDetails.FromChannelBalance, request.PaymentDetails);
        Assert.Equal(bytes, LiquidityAdsCodec.EncodeRequestFunding(request));
    }

    [Fact]
    public void Given_ARequestPaidFromFutureHtlcs_When_Decoded_Then_ItsTypeAndHashesAreKept()
    {
        // Arrange
        var bytes = Convert.FromHexString(LiquidityAdsEclairVectors.OpenRequestFromFutureHtlc);

        // Act
        var decoded = LiquidityAdsCodec.TryDecodeRequestFunding(bytes, out var request);

        // Assert
        Assert.True(decoded);
        Assert.Equal(128UL, request!.PaymentDetails.Type);
        Assert.Equal(LiquidityPaymentType.FromFutureHtlc, request.PaymentDetails.KnownType);
        Assert.Equal(64, request.PaymentDetails.Value.Length);
        Assert.Equal(bytes, LiquidityAdsCodec.EncodeRequestFunding(request));
    }

    [Theory]
    [InlineData(LiquidityAdsEclairVectors.TxAckRbfWillFund, "deadbeef")]
    [InlineData(LiquidityAdsEclairVectors.AcceptWillFund,
                "00202ec38203f4cf37a3b377d9a55c7ae0153c643046dbdbe2ffccfb11b74420103c")]
    public void Given_EclairsWillFund_When_DecodedAndReencoded_Then_ByteExact(string hex, string script)
    {
        // Arrange
        var bytes = Convert.FromHexString(hex);

        // Act
        var decoded = LiquidityAdsCodec.TryDecodeWillFund(bytes, out var willFund);

        // Assert
        Assert.True(decoded);
        Assert.Equal(script, Convert.ToHexStringLower(willFund!.FundingScript));
        Assert.Equal(bytes, LiquidityAdsCodec.EncodeWillFund(willFund));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0001")]
    [InlineData("0001000186a00007a1200226006400001388000003e800")]
    [InlineData("0001000186a00007a1200226006400001388000003e8000101ff")]
    public void Given_MalformedRates_When_Decoded_Then_Refused(string hex)
    {
        Assert.False(LiquidityAdsCodec.TryDecodeWillFundRates(Convert.FromHexString(hex), out _));
    }

    [Theory]
    [InlineData("000000000000c350000061a80003d09002ee009600000032000001f400")]
    [InlineData("000000000000c350000061a80003d09002ee009600000032000001f4000000")]
    [InlineData("000000000000c350000061a80003d09002ee009600000032000001f400fd0001")]
    public void Given_MalformedRequests_When_Decoded_Then_Refused(string hex)
    {
        Assert.False(LiquidityAdsCodec.TryDecodeRequestFunding(Convert.FromHexString(hex), out _));
    }

    [Theory]
    [InlineData("000061a80003d09002ee009600000032000001f40004deadbeef00")]
    [InlineData("000061a80003d09002ee009600000032000001f40005deadbeef")]
    public void Given_MalformedWillFund_When_Decoded_Then_Refused(string hex)
    {
        Assert.False(LiquidityAdsCodec.TryDecodeWillFund(Convert.FromHexString(hex), out _));
    }
}