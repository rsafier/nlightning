using System.Text;
using Microsoft.Extensions.Configuration;

namespace NLightning.Daemon.Tests.Extensions;

using Application.Channels.Splicing;
using Daemon.Extensions;
using Domain.Enums;
using Domain.Node.Options;

/// <summary>
/// Splicing plan D13 and NL-520/NL-515 in the default <c>appsettings.json</c>: <c>option_splice</c>,
/// <c>option_quiesce</c> and <c>option_dual_fund</c> are advertised without <c>AllowExperimentalFeatures</c> on every
/// network, the splice RBF recency rule is the block rule (<c>Splice:MinRbfBlocks</c> 1), the wave spr RBF and auto-bump
/// keys are written with their defaults (<c>AutoBumpAfterBlocks</c> 0 = off), and an operator's
/// <c>Splice:MinRbfInterval</c> from before NL-520 still binds and replaces the block rule.
/// </summary>
public class SpliceConfigTemplateTests
{
    [Theory]
    [InlineData("mainnet")]
    [InlineData("testnet")]
    [InlineData("signet")]
    [InlineData("mutinynet")]
    [InlineData("regtest")]
    public void Given_DefaultConfigJson_When_Bound_Then_SplicingIsAdvertisedAndTheRbfSettingsAreTheDefaults(string network)
    {
        // Arrange
        var configuration = Build(NodeConfigurationExtensions.CreateDefaultConfigJson(network));

        // Act
        var splice = configuration.GetSection(SpliceOptions.SectionName).Get<SpliceOptions>();
        var node = configuration.GetSection("Node").Get<NodeOptions>();

        // Assert
        Assert.NotNull(splice);
        Assert.Equal(1u, splice.MinRbfBlocks);
        Assert.Null(splice.MinRbfInterval);
        Assert.Equal(8, splice.MaxRbfAttempts);
        Assert.Equal(50_000ul, splice.MaxRbfFeeShareSatoshis);
        Assert.Equal(0u, splice.AutoBumpAfterBlocks);
        Assert.Equal(25_000u, splice.AutoBumpMaxFeeratePerKw);
        Assert.Equal(100_000ul, splice.AutoBumpMaxFeeSat);
        Assert.Equal(TimeSpan.FromSeconds(120), splice.AutoBumpMaxWait);
        Assert.NotNull(node);
        Assert.False(node.Features.AllowExperimentalFeatures);
        Assert.Empty(node.Features.GetValidationErrors());
        var features = node.Features.GetNodeFeatures();
        foreach (var feature in new[] { Feature.OptionSplice, Feature.OptionQuiesce, Feature.OptionDualFund })
            Assert.True(features.IsFeatureSet(feature, false), $"{feature} is not advertised on {network}");
    }

    [Fact]
    public void Given_AnOldMinRbfIntervalOverride_When_Bound_Then_ItStillBindsAndReplacesTheBlockRule()
    {
        // Arrange: a Mutinynet rehearsal node's override from before NL-520
        var configuration = Build("""{ "Splice": { "MinRbfInterval": "00:00:05" } }""");

        // Act
        var splice = configuration.GetSection(SpliceOptions.SectionName).Get<SpliceOptions>();

        // Assert
        Assert.NotNull(splice);
        Assert.Equal(TimeSpan.FromSeconds(5), splice.MinRbfInterval);
        Assert.Equal(1u, splice.MinRbfBlocks);
    }

    private static IConfigurationRoot Build(string json) =>
        new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();
}