using System.Text;
using Microsoft.Extensions.Configuration;

namespace NLightning.Daemon.Tests.Extensions;

using Daemon.Extensions;
using Domain.Node.Options;

/// <summary>
/// Liquidity ads (NL-771) in the default <c>appsettings.json</c>: the <c>Node:LiquidityAds</c> section is listed on
/// every network with no rates (decision D-L3: we do not sell until rates are configured) and its default limits.
/// </summary>
public class LiquidityAdsConfigTemplateTests
{
    [Theory]
    [InlineData("mainnet")]
    [InlineData("testnet")]
    [InlineData("signet")]
    [InlineData("mutinynet")]
    [InlineData("regtest")]
    public void Given_DefaultConfigJson_When_Bound_Then_WeDoNotSellAndTheLimitsAreTheDefaults(string network)
    {
        // Arrange
        var json = NodeConfigurationExtensions.CreateDefaultConfigJson(network);
        var configuration = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
                                                      .Build();

        // Act
        var node = configuration.GetSection("Node").Get<NodeOptions>();

        // Assert
        Assert.NotNull(node);
        Assert.True(configuration.GetSection("Node:LiquidityAds").Exists());
        Assert.Equal("4", configuration["Node:LiquidityAds:MaxConcurrentSales"]);
        Assert.Equal("1", configuration["Node:LiquidityAds:MaxSalesPerPeer"]);
        Assert.Equal("4032", configuration["Node:LiquidityAds:LeaseBlocks"]);
        Assert.False(node.LiquidityAds.IsSelling);
        Assert.Null(node.LiquidityAds.GetWillFundRates());
        Assert.Null(node.LiquidityAds.MaxFeeSat);
        Assert.Equal(LiquidityAdsOptions.DefaultMaxConcurrentSales, node.LiquidityAds.MaxConcurrentSales);
        Assert.Equal(LiquidityAdsOptions.DefaultMaxSalesPerPeer, node.LiquidityAds.MaxSalesPerPeer);
        Assert.Empty(node.LiquidityAds.GetValidationErrors());
    }
}