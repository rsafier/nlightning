using System.Text;
using Microsoft.Extensions.Configuration;

namespace NLightning.Daemon.Tests.Extensions;

using Daemon.Extensions;
using Domain.Node.Options;

/// <summary>
/// NL-550, NL-552, NL-562 and NL-564 in the default <c>appsettings.json</c>: the largest peer <c>to_self_delay</c> we
/// accept, the floor of a peer's <c>max_htlc_value_in_flight_msat</c>, the cap of a peer's reserve and the floor of the
/// commitment feerate we offer are listed, with their defaults, on every network.
/// </summary>
public class OpenPolicyConfigTemplateTests
{
    [Theory]
    [InlineData("mainnet")]
    [InlineData("testnet")]
    [InlineData("signet")]
    [InlineData("mutinynet")]
    [InlineData("regtest")]
    public void Given_DefaultConfigJson_When_Bound_Then_TheAcceptedOpenLimitsAreTheDefaults(string network)
    {
        // Arrange
        var json = NodeConfigurationExtensions.CreateDefaultConfigJson(network);
        var configuration = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
                                                      .Build();

        // Act
        var node = configuration.GetSection("Node").Get<NodeOptions>();

        // Assert
        Assert.NotNull(node);
        Assert.Equal("2016", configuration["Node:MaxAcceptedToSelfDelay"]);
        Assert.Equal("1", configuration["Node:MinAcceptedMaxHtlcValueInFlightPercent"]);
        Assert.Equal(NodeOptions.DefaultMaxAcceptedToSelfDelay, node.MaxAcceptedToSelfDelay);
        Assert.Equal(NodeOptions.DefaultMinAcceptedMaxHtlcValueInFlightPercent,
                     node.MinAcceptedMaxHtlcValueInFlightPercent);
        Assert.Equal("10", configuration["Node:MaxAcceptedChannelReservePercent"]);
        Assert.Equal("275", configuration["Node:MinCommitmentFeeRatePerKw"]);
        Assert.Equal(NodeOptions.DefaultMaxAcceptedChannelReservePercent, node.MaxAcceptedChannelReservePercent);
        Assert.Equal(NodeOptions.DefaultMinCommitmentFeeRatePerKw, node.MinCommitmentFeeRatePerKw);
    }
}