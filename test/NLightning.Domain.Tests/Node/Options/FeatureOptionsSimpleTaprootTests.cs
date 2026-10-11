namespace NLightning.Domain.Tests.Node.Options;

using Domain.Node;
using Domain.Node.Options;
using Enums;

/// <summary>
/// <c>option_simple_taproot</c> (bits 80/81, NL-877): off by default, experimental, advertised only with the opt-in.
/// </summary>
public class FeatureOptionsSimpleTaprootTests
{
    [Fact]
    public void Given_DefaultOptions_When_GetNodeFeatures_Then_SimpleTaprootIsNotAdvertised()
    {
        // Arrange
        var options = new FeatureOptions();

        // Act
        var init = options.GetNodeFeatures(FeatureContext.Init);
        var nodeAnnouncement = options.GetNodeFeatures(FeatureContext.NodeAnnouncement);

        // Assert
        Assert.Equal(FeatureSupport.No, options.OptionSimpleTaproot);
        Assert.Contains(Feature.OptionSimpleTaproot, FeatureOptions.ExperimentalFeatures);
        Assert.False(init.HasFeature(Feature.OptionSimpleTaproot));
        Assert.False(nodeAnnouncement.HasFeature(Feature.OptionSimpleTaproot));
        Assert.DoesNotContain(80, init.GetSetBits());
        Assert.DoesNotContain(81, init.GetSetBits());
    }

    [Fact]
    public void Given_SimpleTaprootEnabledWithoutOptIn_When_Validating_Then_ErrorAndNotAdvertised()
    {
        // Arrange
        var options = new FeatureOptions { OptionSimpleTaproot = FeatureSupport.Optional };

        // Act
        var errors = options.GetValidationErrors();
        var init = options.GetNodeFeatures(FeatureContext.Init);

        // Assert
        var error = Assert.Single(errors);
        Assert.Contains(nameof(Feature.OptionSimpleTaproot), error);
        Assert.Contains(nameof(FeatureOptions.AllowExperimentalFeatures), error);
        Assert.False(init.HasFeature(Feature.OptionSimpleTaproot));
    }

    [Theory]
    [InlineData(FeatureSupport.Optional, false)]
    [InlineData(FeatureSupport.Compulsory, true)]
    public void Given_SimpleTaprootEnabledWithOptIn_When_GetNodeFeatures_Then_AdvertisedInInitAndNode(
        FeatureSupport support, bool compulsory)
    {
        // Arrange
        var options = new FeatureOptions { AllowExperimentalFeatures = true, OptionSimpleTaproot = support };

        // Act
        var errors = options.GetValidationErrors();
        var init = options.GetNodeFeatures(FeatureContext.Init);
        var nodeAnnouncement = options.GetNodeFeatures(FeatureContext.NodeAnnouncement);
        var invoice = options.GetNodeFeatures(FeatureContext.Invoice);

        // Assert (bit 80 compulsory, 81 optional; never the staging 180/181; LND wants 23 and 45 next to it)
        Assert.Empty(errors);
        Assert.True(init.IsFeatureSet(Feature.OptionSimpleTaproot, compulsory));
        Assert.False(init.IsFeatureSet(Feature.OptionSimpleTaproot, !compulsory));
        Assert.Contains(compulsory ? 80 : 81, init.GetSetBits());
        Assert.DoesNotContain(180, init.GetSetBits());
        Assert.DoesNotContain(181, init.GetSetBits());
        Assert.True(nodeAnnouncement.IsFeatureSet(Feature.OptionSimpleTaproot, compulsory));
        Assert.False(invoice.HasFeature(Feature.OptionSimpleTaproot));
        Assert.True(init.HasFeature(Feature.OptionSimpleClose));
        Assert.True(init.HasFeature(Feature.OptionChannelType));
        Assert.True(init.HasFeature(Feature.OptionAnchors));
        Assert.True(init.AreDependenciesSet());
    }

    [Fact]
    public void Given_SimpleTaprootWithSimpleCloseDisabled_When_Validating_Then_DependencyError()
    {
        // Arrange
        var options = new FeatureOptions
        {
            AllowExperimentalFeatures = true,
            OptionSimpleTaproot = FeatureSupport.Optional,
            OptionSimpleClose = FeatureSupport.No
        };

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        var error = Assert.Single(errors);
        Assert.Contains($"{Feature.OptionSimpleTaproot} requires {Feature.OptionSimpleClose}", error);
    }

    [Fact]
    public void Given_SimpleTaprootWithAnchorsDisabled_When_Validating_Then_AnchorsError()
    {
        // Arrange (not a BOLT 9 dependency, but LND 0.21 refuses 81 without 23)
        var options = new FeatureOptions
        {
            AllowExperimentalFeatures = true,
            OptionSimpleTaproot = FeatureSupport.Optional,
            OptionAnchors = FeatureSupport.No
        };

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        var error = Assert.Single(errors);
        Assert.Contains($"{Feature.OptionSimpleTaproot} requires {Feature.OptionAnchors}", error);
    }

    [Theory]
    [InlineData(81, FeatureSupport.Optional)]
    [InlineData(80, FeatureSupport.Compulsory)]
    public void Given_PeerFeatureSetWithTaprootBit_When_GetNodeOptions_Then_SimpleTaprootIsRead(int bit,
        FeatureSupport expected)
    {
        // Arrange
        var featureSet = new FeatureSet();
        featureSet.SetFeature(Feature.OptionSimpleClose, false);
        featureSet.SetFeature(bit, true);

        // Act
        var options = FeatureOptions.GetNodeOptions(featureSet, null);

        // Assert
        Assert.Equal(expected, options.OptionSimpleTaproot);
    }
}