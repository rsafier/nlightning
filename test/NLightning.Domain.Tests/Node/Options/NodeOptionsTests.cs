using System.Text;
using Microsoft.Extensions.Configuration;

namespace NLightning.Domain.Tests.Node.Options;

using Domain.Node.Options;
using Domain.Protocol.ValueObjects;

public class NodeOptionsTests
{
    [Fact]
    public void Given_Regtest_When_EnableHtlcsUnset_Then_HtlcsEnabled()
    {
        // Arrange
        var options = new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest };

        // Assert
        Assert.Null(options.EnableHtlcs);
        Assert.True(options.HtlcsEnabled);
    }

    [Fact]
    public void Given_RegtestSpelledInUpperCase_When_EnableHtlcsUnset_Then_HtlcsEnabled()
    {
        // Arrange
        var options = new NodeOptions { BitcoinNetwork = new BitcoinNetwork("Regtest") };

        // Assert
        Assert.True(options.HtlcsEnabled);
    }

    [Theory]
    [InlineData("mainnet")]
    [InlineData("testnet")]
    [InlineData("signet")]
    public void Given_NotRegtest_When_EnableHtlcsUnset_Then_HtlcsDisabled(string network)
    {
        // Arrange
        var options = new NodeOptions { BitcoinNetwork = new BitcoinNetwork(network) };

        // Assert
        Assert.False(options.HtlcsEnabled);
    }

    [Fact]
    public void Given_DefaultOptions_When_Read_Then_MainnetWithHtlcsDisabled()
    {
        // Arrange
        var options = new NodeOptions();

        // Assert
        Assert.False(options.HtlcsEnabled);
    }

    [Theory]
    [InlineData("mainnet", true)]
    [InlineData("regtest", false)]
    public void Given_EnableHtlcsSet_When_Read_Then_ExplicitValueWins(string network, bool enable)
    {
        // Arrange
        var options = new NodeOptions { BitcoinNetwork = new BitcoinNetwork(network), EnableHtlcs = enable };

        // Assert
        Assert.Equal(enable, options.HtlcsEnabled);
    }

    [Fact]
    public void Given_NetworkChangedAfterConstruction_When_Read_Then_HtlcsEnabledFollowsIt()
    {
        // Arrange (the daemon sets the network in PostConfigure, after binding)
        var options = new NodeOptions
        {
            // Act
            BitcoinNetwork = BitcoinNetwork.Regtest
        };

        // Assert
        Assert.True(options.HtlcsEnabled);
    }

    [Fact]
    public void Given_DefaultOptions_When_GetValidationErrors_Then_NoErrors()
    {
        // Arrange
        var options = new NodeOptions();

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Empty(errors);
        Assert.Equal(TimeSpan.FromSeconds(5), options.ReconnectInitialDelay);
        Assert.Equal(TimeSpan.FromMinutes(10), options.ReconnectMaxDelay);
    }

    [Fact]
    public void Given_NonPositiveReconnectDelay_When_GetValidationErrors_Then_Error()
    {
        // Arrange
        var options = new NodeOptions { ReconnectInitialDelay = TimeSpan.Zero };

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Contains(errors, e => e.Contains(nameof(NodeOptions.ReconnectInitialDelay)));
    }

    [Fact]
    public void Given_MaxReconnectDelayBelowInitial_When_GetValidationErrors_Then_Error()
    {
        // Arrange
        var options = new NodeOptions
        {
            ReconnectInitialDelay = TimeSpan.FromSeconds(30),
            ReconnectMaxDelay = TimeSpan.FromSeconds(10)
        };

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Contains(errors, e => e.Contains(nameof(NodeOptions.ReconnectMaxDelay)));
    }

    [Fact]
    public void Given_InvalidRoutingOptions_When_GetValidationErrors_Then_RoutingErrorsIncluded()
    {
        // Arrange
        var options = new NodeOptions { Routing = new RoutingOptions { CltvExpiryDelta = 33 } };

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Contains(errors, e => e.Contains(nameof(RoutingOptions.CltvExpiryDelta)));
    }

    [Fact]
    public void Given_NodeSectionJson_When_Bound_Then_HtlcReconnectAndRoutingValuesAreRead()
    {
        // Arrange (the shape of ~/.nltg/<network>/appsettings.json)
        const string json = """
                            {
                              "Node": {
                                "EnableHtlcs": false,
                                "ReconnectInitialDelay": "00:00:01",
                                "ReconnectMaxDelay": "00:00:30",
                                "Routing": {
                                  "FeeBaseMsat": 2000,
                                  "FeeProportionalMillionths": 500,
                                  "CltvExpiryDelta": 40,
                                  "MaxCltvExpiryDistance": 1008,
                                  "ExpiryTooSoonBlocks": 12,
                                  "InvoiceMinFinalCltvExpiry": 36,
                                  "InvoiceExpirySeconds": 600,
                                  "HtlcMinimumMsat": 1,
                                  "HtlcMaximumMsat": 1000000000
                                }
                              }
                            }
                            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();

        // Act
        var options = configuration.GetSection("Node").Get<NodeOptions>();

        // Assert
        Assert.NotNull(options);
        Assert.False(options.EnableHtlcs);
        Assert.False(options.HtlcsEnabled);
        Assert.Equal(TimeSpan.FromSeconds(1), options.ReconnectInitialDelay);
        Assert.Equal(TimeSpan.FromSeconds(30), options.ReconnectMaxDelay);
        Assert.Equal(2_000UL, options.Routing.FeeBaseMsat);
        Assert.Equal(500U, options.Routing.FeeProportionalMillionths);
        Assert.Equal((ushort)40, options.Routing.CltvExpiryDelta);
        Assert.Equal(1_008U, options.Routing.MaxCltvExpiryDistance);
        Assert.Equal((ushort)12, options.Routing.ExpiryTooSoonBlocks);
        Assert.Equal((ushort)36, options.Routing.InvoiceMinFinalCltvExpiry);
        Assert.Equal(600U, options.Routing.InvoiceExpirySeconds);
        Assert.Equal(1UL, options.Routing.HtlcMinimumMsat);
        Assert.Equal(1_000_000_000UL, options.Routing.HtlcMaximumMsat);
        Assert.Empty(options.GetValidationErrors());
    }

    [Fact]
    public void Given_EnvironmentStyleKeys_When_Bound_Then_NestedRoutingValuesAreRead()
    {
        // Arrange (NLTG_Node__Routing__CltvExpiryDelta etc. arrive as "Node:Routing:CltvExpiryDelta")
        var configuration = new ConfigurationBuilder()
                           .AddInMemoryCollection(new Dictionary<string, string?>
                           {
                               ["Node:EnableHtlcs"] = "true",
                               ["Node:Routing:CltvExpiryDelta"] = "33",
                               ["Node:Routing:FeeBaseMsat"] = "1000"
                           })
                           .Build();

        // Act
        var options = configuration.GetSection("Node").Get<NodeOptions>();

        // Assert
        Assert.NotNull(options);
        Assert.True(options.HtlcsEnabled);
        Assert.Equal((ushort)33, options.Routing.CltvExpiryDelta);
        Assert.Contains(options.GetValidationErrors(), e => e.Contains(nameof(RoutingOptions.CltvExpiryDelta)));
    }

    [Fact]
    public void Given_NodeSectionWithoutNewKeys_When_Bound_Then_DefaultsApply()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
                           .AddInMemoryCollection(new Dictionary<string, string?>
                           {
                               ["Node:ToSelfDelay"] = "144"
                           })
                           .Build();

        // Act
        var options = configuration.GetSection("Node").Get<NodeOptions>();

        // Assert
        Assert.NotNull(options);
        Assert.Null(options.EnableHtlcs);
        Assert.Equal(TimeSpan.FromSeconds(5), options.ReconnectInitialDelay);
        Assert.Equal((ushort)40, options.Routing.CltvExpiryDelta);
        Assert.Empty(options.GetValidationErrors());
    }
}