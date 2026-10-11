using System.Text;
using Microsoft.Extensions.Configuration;

namespace NLightning.Domain.Tests.Node.Options;

using Domain.LiquidityAds.Constants;
using Domain.LiquidityAds.Enums;
using Domain.LiquidityAds.Models;
using Domain.Node.Options;

public class LiquidityAdsOptionsTests
{
    [Fact]
    public void Given_DefaultOptions_When_Read_Then_WeDoNotSellAndTheyAreValid()
    {
        // Arrange (decision D-L3: selling is off until rates are configured)
        var options = new NodeOptions();

        // Assert
        Assert.Empty(options.LiquidityAds.FundingRates);
        Assert.False(options.LiquidityAds.IsSelling);
        Assert.Null(options.LiquidityAds.GetWillFundRates());
        Assert.Equal(4, options.LiquidityAds.MaxConcurrentSales);
        Assert.Equal(1, options.LiquidityAds.MaxSalesPerPeer);
        Assert.Equal(LiquidityAdsConstants.LeaseBlocks, options.LiquidityAds.LeaseBlocks);
        Assert.Null(options.LiquidityAds.MaxFeeSat);
        Assert.Empty(options.LiquidityAds.GetValidationErrors());
        Assert.Empty(options.GetValidationErrors());
    }

    [Fact]
    public void Given_ConfiguredRates_When_RatesAreRead_Then_TheyAreTheRatesWithChannelBalanceOnly()
    {
        // Arrange
        var options = new LiquidityAdsOptions
        {
            FundingRates =
            [
                Rate(100_000, 500_000, 550, 100, 5_000, 1_000),
                Rate(500_000, 5_000_000, 1_100, 75, 0, 1_500)
            ]
        };

        // Act
        var rates = options.GetWillFundRates();

        // Assert (decision D-L2: only from_channel_balance; Eclair's init vector shape)
        Assert.NotNull(rates);
        Assert.True(options.IsSelling);
        Assert.Equal([
                         new FundingRate(100_000, 500_000, 550, 100, 5_000, 1_000),
                         new FundingRate(500_000, 5_000_000, 1_100, 75, 0, 1_500)
                     ], rates.Rates);
        Assert.Equal(new byte[] { 0x01 }, rates.EncodedPaymentTypes);
        Assert.True(rates.Supports(LiquidityPaymentType.FromChannelBalance));
        Assert.False(rates.Supports(LiquidityPaymentType.FromFutureHtlc));
    }

    [Fact]
    public void Given_TheNodeSection_When_Bound_Then_TheRatesAndLimitsAreRead()
    {
        // Arrange
        const string json = """
                            {
                              "Node": {
                                "LiquidityAds": {
                                  "FundingRates": [
                                    { "MinAmountSat": 100000, "MaxAmountSat": 500000, "FundingWeight": 550,
                                      "FeeBasis": 100, "FeeBaseSat": 5000, "ChannelCreationFeeSat": 1000 }
                                  ],
                                  "MaxConcurrentSales": 8,
                                  "MaxSalesPerPeer": 2,
                                  "LeaseBlocks": 2016,
                                  "MaxFeeSat": 20000
                                }
                              }
                            }
                            """;
        var configuration = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
                                                      .Build();

        // Act
        var node = configuration.GetSection("Node").Get<NodeOptions>();

        // Assert
        Assert.NotNull(node);
        var rate = Assert.Single(node.LiquidityAds.FundingRates);
        Assert.Equal(new FundingRate(100_000, 500_000, 550, 100, 5_000, 1_000), rate.ToFundingRate());
        Assert.Equal(8, node.LiquidityAds.MaxConcurrentSales);
        Assert.Equal(2, node.LiquidityAds.MaxSalesPerPeer);
        Assert.Equal(2016u, node.LiquidityAds.LeaseBlocks);
        Assert.Equal(20_000ul, node.LiquidityAds.MaxFeeSat);
        Assert.Empty(node.GetValidationErrors());
    }

    [Fact]
    public void Given_AnEmptyRatesArrayAndANullFeeLimit_When_Bound_Then_TheDefaultsStay()
    {
        // Arrange: the daemon template's section
        const string json = """
                            {
                              "Node": {
                                "LiquidityAds": {
                                  "FundingRates": [],
                                  "MaxConcurrentSales": 4,
                                  "MaxSalesPerPeer": 1,
                                  "LeaseBlocks": 4032,
                                  "MaxFeeSat": null
                                }
                              }
                            }
                            """;
        var configuration = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
                                                      .Build();

        // Act
        var node = configuration.GetSection("Node").Get<NodeOptions>();

        // Assert
        Assert.NotNull(node);
        Assert.False(node.LiquidityAds.IsSelling);
        Assert.Null(node.LiquidityAds.MaxFeeSat);
        Assert.Empty(node.GetValidationErrors());
    }

    public static TheoryData<Action<LiquidityAdsOptions>, string> InvalidOptions => new()
    {
        { o => o.FundingRates = [Rate(500_000, 100_000, 550, 100, 0, 0)], "MinAmountSat" },
        { o => o.FundingRates = [Rate(0, 0, 550, 100, 0, 0)], "MaxAmountSat" },
        { o => o.FundingRates = [Rate(100_000, 500_000, 550, 10_001, 0, 0)], "FeeBasis" },
        {
            o => o.FundingRates = Enumerable.Range(1, LiquidityAdsOptions.MaxFundingRates + 1)
                                            .Select(i => Rate(1_000, (uint)(i * 10_000), 0, 0, 0, 0)).ToList(),
            "FundingRates"
        },
        { o => o.MaxConcurrentSales = 0, "MaxConcurrentSales" },
        { o => o.MaxSalesPerPeer = 0, "MaxSalesPerPeer" },
        { o => o.MaxSalesPerPeer = 5, "MaxSalesPerPeer" },
        { o => o.LeaseBlocks = 0, "LeaseBlocks" },
        { o => o.MaxFeeSat = 21_000_000UL * 100_000_000UL + 1, "MaxFeeSat" }
    };

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void Given_AnInvalidSetting_When_Validated_Then_ItIsAnErrorOfTheNodeOptions(
        Action<LiquidityAdsOptions> configure, string expected)
    {
        // Arrange
        var node = new NodeOptions();
        configure(node.LiquidityAds);

        // Act
        var errors = node.GetValidationErrors();

        // Assert
        Assert.Contains(errors, e => e.StartsWith("LiquidityAds:") && e.Contains(expected));
    }

    [Fact]
    public void Given_TheMostRatesAtTheLimits_When_Validated_Then_TheyAreValid()
    {
        // Arrange: 16 rates, a min equal to the max and a 100 % proportional fee
        var options = new LiquidityAdsOptions
        {
            FundingRates = Enumerable.Range(1, LiquidityAdsOptions.MaxFundingRates)
                                     .Select(i => Rate((uint)i, (uint)i, ushort.MaxValue, 10_000, uint.MaxValue, 0))
                                     .ToList()
        };

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Empty(errors);
        Assert.Equal(LiquidityAdsOptions.MaxFundingRates, options.GetWillFundRates()!.Rates.Count);
    }

    private static FundingRateOptions Rate(uint min, uint max, ushort weight, ushort basis, uint baseFee,
                                           uint creationFee) =>
        new()
        {
            MinAmountSat = min,
            MaxAmountSat = max,
            FundingWeight = weight,
            FeeBasis = basis,
            FeeBaseSat = baseFee,
            ChannelCreationFeeSat = creationFee
        };
}