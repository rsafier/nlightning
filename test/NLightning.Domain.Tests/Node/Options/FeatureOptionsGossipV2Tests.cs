namespace NLightning.Domain.Tests.Node.Options;

using Domain.Node;
using Domain.Node.Options;
using Enums;

/// <summary>
/// <c>option_gossip_v2</c> (bits 70/71, BOLTs PR #1059 draft, NL-878): off by default, experimental, advertised only
/// with the opt-in; 72-75 are known but never advertised.
/// </summary>
public class FeatureOptionsGossipV2Tests
{
    [Fact]
    public void Given_DefaultOptions_When_GetNodeFeatures_Then_GossipV2IsNotAdvertised()
    {
        // Arrange
        var options = new FeatureOptions();

        // Act
        var init = options.GetNodeFeatures(FeatureContext.Init);

        // Assert
        Assert.Equal(FeatureSupport.No, options.OptionGossipV2);
        Assert.False(options.IsGossipV2Advertised);
        Assert.Contains(Feature.OptionGossipV2, FeatureOptions.ExperimentalFeatures);
        foreach (var bit in new[] { 70, 71, 72, 73, 74, 75 })
            Assert.DoesNotContain(bit, init.GetSetBits());
    }

    [Fact]
    public void Given_GossipV2EnabledWithoutOptIn_When_Validating_Then_ErrorAndNotAdvertised()
    {
        // Arrange
        var options = new FeatureOptions { OptionGossipV2 = FeatureSupport.Optional };

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        var error = Assert.Single(errors);
        Assert.Contains(nameof(Feature.OptionGossipV2), error);
        Assert.False(options.IsGossipV2Advertised);
        Assert.False(options.GetNodeFeatures().HasFeature(Feature.OptionGossipV2));
    }

    [Theory]
    [InlineData(FeatureSupport.Optional, 71)]
    [InlineData(FeatureSupport.Compulsory, 70)]
    public void Given_GossipV2EnabledWithOptIn_When_GetNodeFeatures_Then_AdvertisedInInitAndNodeOnly(
        FeatureSupport support, int bit)
    {
        // Arrange
        var options = new FeatureOptions { AllowExperimentalFeatures = true, OptionGossipV2 = support };

        // Act
        var init = options.GetNodeFeatures(FeatureContext.Init);
        var node = options.GetNodeFeatures(FeatureContext.NodeAnnouncement);
        var invoice = options.GetNodeFeatures(FeatureContext.Invoice);

        // Assert
        Assert.Empty(options.GetValidationErrors());
        Assert.True(options.IsGossipV2Advertised);
        Assert.Contains(bit, init.GetSetBits());
        Assert.Contains(bit, node.GetSetBits());
        Assert.DoesNotContain(bit, invoice.GetSetBits());
        Assert.DoesNotContain(73, init.GetSetBits());
        Assert.DoesNotContain(75, init.GetSetBits());
    }

    [Fact]
    public void Given_APeerWithGossipV2_When_Negotiating_Then_TheNegotiatedOptionsCarryIt()
    {
        // Arrange
        var ours = new FeatureOptions { AllowExperimentalFeatures = true, OptionGossipV2 = FeatureSupport.Optional }
                  .GetNodeFeatures();
        var theirs = new FeatureSet();
        theirs.SetFeature(Feature.OptionGossipV2, false);

        // Act
        var compatible = ours.IsCompatible(theirs, out var negotiated);

        // Assert
        Assert.True(compatible);
        Assert.Equal(FeatureSupport.Optional, FeatureOptions.GetNodeOptions(negotiated!, null).OptionGossipV2);
    }

    [Fact]
    public void Given_APeerWithGossipV2P2wshButNotGossipV2_When_WeDoNotSetIt_Then_ThePeerIsNotRefused()
    {
        // Arrange: a draft peer's 73 without its dependency 71 is read as an unknown odd bit (NL-878, like NL-973)
        var ours = new FeatureSet();
        var theirs = new FeatureSet();
        theirs.SetFeature(Feature.OptionGossipV2P2wsh, false, false);

        // Act
        var compatible = ours.IsCompatible(theirs, out _);

        // Assert
        Assert.True(compatible);
        Assert.Equal([Feature.OptionGossipV2], FeatureSet.GetDependencies(Feature.OptionGossipV2P2wsh));
    }
}