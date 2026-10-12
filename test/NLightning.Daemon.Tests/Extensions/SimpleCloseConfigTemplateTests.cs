using System.Text;
using Microsoft.Extensions.Configuration;

namespace NLightning.Daemon.Tests.Extensions;

using Daemon.Extensions;
using Domain.Enums;
using Domain.Node.Options;

/// <summary>
/// Taproot plan D-T1 (owner decision 2026-10-03, NL-877) in the default <c>appsettings.json</c>:
/// <c>option_simple_close</c> is advertised Optional (bit 61, never 60) without <c>AllowExperimentalFeatures</c> on every
/// network, mainnet included, with its BOLT 9 dependency <c>option_shutdown_anysegwit</c>.
/// </summary>
public class SimpleCloseConfigTemplateTests
{
    [Theory]
    [InlineData("mainnet")]
    [InlineData("testnet")]
    [InlineData("testnet4")]
    [InlineData("signet")]
    [InlineData("mutinynet")]
    [InlineData("regtest")]
    public void Given_DefaultConfigJson_When_Bound_Then_SimpleCloseIsAdvertisedOptional(string network)
    {
        // Arrange
        var json = NodeConfigurationExtensions.CreateDefaultConfigJson(network);
        var configuration = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
                                                      .Build();

        // Act
        var node = configuration.GetSection("Node").Get<NodeOptions>();

        // Assert
        Assert.NotNull(node);
        Assert.False(node.Features.AllowExperimentalFeatures);
        Assert.Empty(node.Features.GetValidationErrors());
        Assert.Equal(FeatureSupport.Optional, node.Features.OptionSimpleClose);
        foreach (var context in new[] { FeatureContext.Init, FeatureContext.NodeAnnouncement })
        {
            var features = node.Features.GetNodeFeatures(context);
            Assert.True(features.IsFeatureSet(Feature.OptionSimpleClose, false),
                        $"option_simple_close is not advertised on {network} ({context})");
            Assert.False(features.IsFeatureSet(Feature.OptionSimpleClose, true));
            Assert.True(features.IsFeatureSet(Feature.OptionShutdownAnySegwit, false));
        }
    }
}