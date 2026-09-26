using System.Text;
using Microsoft.Extensions.Configuration;

namespace NLightning.Domain.Tests.Node.Options;

using Domain.Node.Options;
using Domain.Protocol.ValueObjects;

public class NodeOptionsTests
{
    [Theory]
    [InlineData("mainnet")]
    [InlineData("testnet")]
    [InlineData("signet")]
    [InlineData("regtest")]
    [InlineData("Regtest")]
    public void Given_AnyNetwork_When_EnableHtlcsUnset_Then_HtlcsEnabled(string network)
    {
        // Arrange (BOLT 5 plan O6-T4: the mainnet gate is open, unset means on everywhere)
        var options = new NodeOptions { BitcoinNetwork = new BitcoinNetwork(network) };

        // Assert
        Assert.Null(options.EnableHtlcs);
        Assert.True(options.HtlcsEnabled);
    }

    [Fact]
    public void Given_DefaultOptions_When_Read_Then_MainnetWithHtlcsEnabled()
    {
        // Arrange
        var options = new NodeOptions();

        // Assert
        Assert.Equal("mainnet", options.BitcoinNetwork.Name);
        Assert.True(options.HtlcsEnabled);
    }

    [Theory]
    [InlineData("mainnet")]
    [InlineData("testnet")]
    [InlineData("signet")]
    [InlineData("regtest")]
    public void Given_EnableHtlcsFalse_When_Read_Then_HtlcsDisabledOnEveryNetwork(string network)
    {
        // Arrange
        var options = new NodeOptions { BitcoinNetwork = new BitcoinNetwork(network), EnableHtlcs = false };

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
    public void Given_EnableHtlcsFalseAndNetworkSetAfterBinding_When_Read_Then_StillDisabled()
    {
        // Arrange (the daemon sets the network in PostConfigure, after binding)
        var options = new NodeOptions
        {
            EnableHtlcs = false,
            // Act
            BitcoinNetwork = BitcoinNetwork.Regtest
        };

        // Assert
        Assert.False(options.HtlcsEnabled);
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
    public void Given_CustomSignetOnRegtest_When_GetValidationErrors_Then_Error()
    {
        // Arrange
        var options = new NodeOptions
        {
            BitcoinNetwork = BitcoinNetwork.Regtest,
            CustomSignet = new CustomSignetOptions { Name = "mutinynet" }
        };

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Contains(errors, e => e.Contains("CustomSignet"));
    }

    [Fact]
    public void Given_NodeSectionWithCustomSignet_When_Bound_Then_CustomSignetIsReadAndValid()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
                           .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(
                                                               """{ "Node": { "CustomSignet": { "Name": "mutinynet" } } }""")))
                           .Build();
        var options = new NodeOptions { BitcoinNetwork = BitcoinNetwork.Signet };

        // Act
        configuration.GetSection("Node").Bind(options);

        // Assert
        Assert.Equal("mutinynet", options.CustomSignet?.Name);
        Assert.Empty(options.GetValidationErrors());
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
        Assert.Equal(2_000U, options.Routing.FeeBaseMsat);
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
    public void Given_FeeBaseAboveU32_When_Bound_Then_BindingFails()
    {
        // Arrange (fee_base_msat is a u32 in channel_update and BOLT 11 route hints)
        var configuration = new ConfigurationBuilder()
                           .AddInMemoryCollection(new Dictionary<string, string?>
                           {
                               ["Node:Routing:FeeBaseMsat"] = "4294967296"
                           })
                           .Build();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => configuration.GetSection("Node").Get<NodeOptions>());
    }

    [Fact]
    public void Given_FeeBaseAtU32Max_When_Bound_Then_Accepted()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
                           .AddInMemoryCollection(new Dictionary<string, string?>
                           {
                               ["Node:Routing:FeeBaseMsat"] = "4294967295"
                           })
                           .Build();

        // Act
        var options = configuration.GetSection("Node").Get<NodeOptions>();

        // Assert
        Assert.NotNull(options);
        Assert.Equal(uint.MaxValue, options.Routing.FeeBaseMsat);
        Assert.Empty(options.GetValidationErrors());
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
        Assert.True(options.HtlcsEnabled);
        Assert.Equal(TimeSpan.FromSeconds(5), options.ReconnectInitialDelay);
        Assert.Equal((ushort)40, options.Routing.CltvExpiryDelta);
        Assert.Empty(options.GetValidationErrors());
    }

    [Theory]
    [InlineData("3399ff", new byte[] { 0x33, 0x99, 0xFF })]
    [InlineData("#FF0000", new byte[] { 0xFF, 0x00, 0x00 })]
    [InlineData(" 00a1b2 ", new byte[] { 0x00, 0xA1, 0xB2 })]
    public void Given_AColor_When_Read_Then_ItIsTheRgbBytes(string color, byte[] expected)
    {
        // Arrange
        var options = new NodeOptions { Color = color };

        // Act / Assert
        Assert.Equal(expected, options.GetColorBytes());
        Assert.Empty(options.GetValidationErrors());
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("zz99ff")]
    public void Given_ABadColor_When_Validated_Then_ItIsAnError(string color)
    {
        // Arrange
        var options = new NodeOptions { Color = color };

        // Act / Assert
        Assert.Throws<FormatException>(() => options.GetColorBytes());
        Assert.Contains(options.GetValidationErrors(), e => e.Contains("Node:Color"));
    }

    [Fact]
    public void Given_AnAliasOfMoreThan32Utf8Bytes_When_Validated_Then_ItIsAnError()
    {
        // Arrange: 11 three-byte characters are 33 bytes (BOLT 7: alias is 32 bytes)
        var options = new NodeOptions { Alias = new string('\u20AC', 11) };
        var fits = new NodeOptions { Alias = new string('\u20AC', 10) + "ab" };

        // Act / Assert
        Assert.Contains(options.GetValidationErrors(), e => e.Contains(nameof(NodeOptions.Alias)));
        Assert.Empty(fits.GetValidationErrors());
        Assert.Equal(32, Encoding.UTF8.GetByteCount(fits.Alias));
    }

    [Fact]
    public void Given_Defaults_When_Read_Then_EmptyAliasAndLndColor()
    {
        // Arrange
        var options = new NodeOptions();

        // Act / Assert
        Assert.Equal(string.Empty, options.Alias);
        Assert.Equal(NodeOptions.DefaultColor, options.Color);
    }
}