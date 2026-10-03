using NLightning.Tests.Utils.Vectors;

namespace NLightning.Domain.Tests.LiquidityAds;

using Domain.Crypto.ValueObjects;
using Domain.LiquidityAds;
using Domain.LiquidityAds.Enums;
using Domain.LiquidityAds.Models;

public class LiquidityAdsRulesTests
{
    // Eclair's LiquidityAdsSpec rate
    private static readonly FundingRate s_rate = new(100_000, 1_000_000, 500, 100, 10, 1_000);
    private static readonly byte[] s_script = Convert.FromHexString(LiquidityAdsEclairVectors.SpecFundingScript);

    [Theory]
    // FeeratePerByte(5 sat).perKw = 1250 sat/kw, FeeratePerByte(10 sat).perKw = 2500 sat/kw
    [InlineData(1_250U, 500_000UL, 500_000UL, false, 5_635UL)]
    [InlineData(1_250U, 500_000UL, 600_000UL, false, 5_635UL)]
    [InlineData(1_250U, 500_000UL, 600_000UL, true, 6_635UL)]
    [InlineData(1_250U, 500_000UL, 400_000UL, false, 4_635UL)]
    [InlineData(2_500U, 500_000UL, 500_000UL, false, 6_260UL)]
    public void Given_EclairsFeeTable_When_Computed_Then_TheTotalsMatch(uint feerate, ulong requested,
                                                                         ulong contributed, bool creation,
                                                                         ulong expectedTotal)
    {
        // Act
        var fees = LiquidityAdsRules.ComputeFees(s_rate, feerate, requested, contributed, creation);

        // Assert
        Assert.Equal(expectedTotal, fees.TotalSat);
        Assert.Equal(expectedTotal * 1_000, fees.TotalMsat);
        Assert.Equal((ulong)feerate * 500 / 1_000, fees.MiningFeeSat);
    }

    [Fact]
    public void Given_AProportionalFeeBelowASatoshi_When_Computed_Then_RoundedDown()
    {
        // Arrange: 1 basis point of 9,999 sat is 999.9 msat
        var rate = new FundingRate(1, 1_000_000, 0, 1, 0, 0);

        // Act
        var fees = LiquidityAdsRules.ComputeFees(rate, 1_000, 9_999, 9_999, false);

        // Assert
        Assert.Equal(0UL, fees.TotalSat);
    }

    [Fact]
    public void Given_TheSellersRates_When_ARequestIsCreated_Then_ItTakesTheFirstCompatibleRate()
    {
        // Arrange
        var low = new FundingRate(100_000, 500_000, 550, 100, 5_000, 1_000);
        var high = new FundingRate(500_000, 5_000_000, 1_100, 75, 0, 1_500);
        var rates = WillFundRates.Create([low, high], [LiquidityPaymentType.FromChannelBalance]);

        // Act / Assert
        Assert.Equal(low, LiquidityAdsRules.CreateRequest(rates, 500_000)!.Rate);
        Assert.Equal(high, LiquidityAdsRules.CreateRequest(rates, 750_000)!.Rate);
        Assert.Null(LiquidityAdsRules.CreateRequest(rates, 50_000));
        Assert.Null(LiquidityAdsRules.CreateRequest(
                        WillFundRates.Create([low], [LiquidityPaymentType.FromFutureHtlc]), 200_000));
    }

    [Fact]
    public void Given_ARequestOffTheSellersRates_When_Validated_Then_EachRefusalIsNamed()
    {
        // Arrange
        var ours = WillFundRates.Create([s_rate], [LiquidityPaymentType.FromChannelBalance]);
        var valid = new RequestFunding(500_000, s_rate, LiquidityPaymentDetails.FromChannelBalance);

        // Act / Assert
        Assert.Equal(LiquidityAdsRefusal.None, LiquidityAdsRules.ValidateRequest(ours, valid));
        Assert.Equal(LiquidityAdsRefusal.UnknownRate,
                     LiquidityAdsRules.ValidateRequest(ours, valid with { Rate = s_rate with { FeeBasis = 99 } }));
        Assert.Equal(LiquidityAdsRefusal.AmountOutOfRange,
                     LiquidityAdsRules.ValidateRequest(ours, valid with { RequestedSat = 99_999 }));
        Assert.Equal(LiquidityAdsRefusal.AmountOutOfRange,
                     LiquidityAdsRules.ValidateRequest(ours, valid with { RequestedSat = 1_000_001 }));
        Assert.Equal(LiquidityAdsRefusal.UnsupportedPaymentType,
                     LiquidityAdsRules.ValidateRequest(
                         ours, valid with { PaymentDetails = LiquidityPaymentDetails.Create(128, new byte[32]) }));

        // A seller that lists an on-the-fly type still refuses it: this node only implements the channel balance
        var withFuture = WillFundRates.Create([s_rate], Enum.GetValues<LiquidityPaymentType>());
        Assert.Equal(LiquidityAdsRefusal.UnsupportedPaymentType,
                     LiquidityAdsRules.ValidateRequest(
                         withFuture, valid with { PaymentDetails = LiquidityPaymentDetails.Create(128, new byte[32]) }));
    }

    [Fact]
    public void Given_TheSellersAnswer_When_Validated_Then_EclairsOrderOfChecksApplies()
    {
        // Arrange
        var request = new RequestFunding(500_000, s_rate, LiquidityPaymentDetails.FromChannelBalance);
        var signature = new CompactSignature(Convert.FromHexString(LiquidityAdsEclairVectors.SpecSignature));
        var willFund = new WillFund(s_rate, s_script, signature);
        var expectedHash = LiquidityAdsRules.SignedData(s_rate, s_script);
        bool Verify(Hash hash, CompactSignature sig) =>
            ((ReadOnlySpan<byte>)hash).SequenceEqual(expectedHash) && ((ReadOnlySpan<byte>)sig).SequenceEqual(signature);

        // Act / Assert
        Assert.Equal(LiquidityAdsRefusal.None,
                     LiquidityAdsRules.ValidateWillFund(request, willFund, s_script, 500_000, 2_500, true, Verify, null,
                                                        out var fees));
        Assert.Equal(1_250UL + 10 + 1_000 + 5_000, fees.TotalSat);
        Assert.Equal(LiquidityAdsRefusal.Missing,
                     LiquidityAdsRules.ValidateWillFund(request, null, s_script, 500_000, 2_500, true, Verify, null,
                                                        out _));
        Assert.Equal(LiquidityAdsRefusal.BadSignature,
                     LiquidityAdsRules.ValidateWillFund(request, new WillFund(s_rate, s_script, new byte[64]), s_script,
                                                        500_000, 2_500, true, Verify, null, out _));
        Assert.Equal(LiquidityAdsRefusal.BadSignature,
                     LiquidityAdsRules.ValidateWillFund(request, willFund, Convert.FromHexString("deadbeef"), 500_000,
                                                        2_500, true, Verify, null, out _));
        Assert.Equal(LiquidityAdsRefusal.AmountTooLow,
                     LiquidityAdsRules.ValidateWillFund(request, willFund, s_script, 0, 2_500, true, Verify, null,
                                                        out _));
        Assert.Equal(LiquidityAdsRefusal.RateMismatch,
                     LiquidityAdsRules.ValidateWillFund(request,
                                                        new WillFund(s_rate with { FeeBaseSat = 0 }, s_script,
                                                                     signature), s_script, 500_000, 2_500, true,
                                                        Verify, null, out _));
        Assert.Equal(LiquidityAdsRefusal.FeeTooHigh,
                     LiquidityAdsRules.ValidateWillFund(request, willFund, s_script, 500_000, 2_500, true, Verify,
                                                        7_259, out _));
        Assert.Equal(LiquidityAdsRefusal.None,
                     LiquidityAdsRules.ValidateWillFund(request, willFund, s_script, 500_000, 2_500, true, Verify,
                                                        7_260, out _));
    }

    [Fact]
    public void Given_ARateAndScript_When_SignedDataIsComputed_Then_ItIsTheShaOfTagRateAndScript()
    {
        // Arrange
        var expected = System.Security.Cryptography.SHA256.HashData(
            [.. "liquidity_ads_purchase"u8, .. LiquidityAdsCodec.EncodeFundingRate(s_rate), .. s_script]);

        // Act
        var hash = LiquidityAdsRules.SignedData(s_rate, s_script);

        // Assert
        Assert.Equal(expected, (byte[])hash);
    }
}