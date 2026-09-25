namespace NLightning.Domain.Tests.Node.Options;

using Domain.Node.Options;
using Enums;

public class FeatureOptionsTests
{
    [Fact]
    public void Given_DefaultOptions_When_GetValidationErrors_Then_NoErrors()
    {
        // Arrange
        var options = new FeatureOptions();

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Empty(errors);
    }

    [Fact]
    public void Given_FeatureEnabledAndDependencyDisabled_When_GetValidationErrors_Then_ErrorIsReturned()
    {
        // Arrange
        var options = new FeatureOptions
        {
            ZeroConf = FeatureSupport.Optional,
            ScidAlias = FeatureSupport.No
        };

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        var error = Assert.Single(errors);
        Assert.Contains(nameof(Feature.OptionZeroconf), error);
        Assert.Contains(nameof(Feature.OptionScidAlias), error);
    }

    [Fact]
    public void Given_InvoiceOnlyFeature_When_GetNodeFeaturesForInit_Then_FeatureIsNotAdvertised()
    {
        // Arrange
        var options = new FeatureOptions { PaymentMetadata = FeatureSupport.Optional };

        // Act
        var initFeatures = options.GetNodeFeatures();
        var invoiceFeatures = options.GetNodeFeatures(FeatureContext.Invoice);

        // Assert
        Assert.False(initFeatures.HasFeature(Feature.OptionPaymentMetadata));
        Assert.True(invoiceFeatures.IsFeatureSet(Feature.OptionPaymentMetadata, false));
    }

    [Fact]
    public void Given_DefaultOptions_When_GetNodeFeatures_Then_DependenciesAreSet()
    {
        // Arrange
        var options = new FeatureOptions();

        // Act
        var features = options.GetNodeFeatures();

        // Assert
        Assert.True(features.AreDependenciesSet());
    }

    [Theory]
    [InlineData(Feature.GossipQueriesEx)]
    [InlineData(Feature.BasicMpp)]
    [InlineData(Feature.OptionRouteBlinding)]
    [InlineData(Feature.OptionDualFund)]
    [InlineData(Feature.OptionQuiesce)]
    [InlineData(Feature.OptionAttributionData)]
    [InlineData(Feature.OptionProvideStorage)]
    [InlineData(Feature.OptionScidAlias)]
    [InlineData(Feature.OptionUpfrontShutdownScript)]
    public void Given_DefaultOptions_When_GetNodeFeatures_Then_UnimplementedFeatureIsNotAdvertised(Feature feature)
    {
        // Arrange
        var options = new FeatureOptions();

        // Act
        var features = options.GetNodeFeatures();

        // Assert
        Assert.False(features.HasFeature(feature));
    }

    [Fact]
    public void Given_DefaultOptions_When_GetNodeFeatures_Then_DataLossProtectIsOptional()
    {
        // Arrange
        var options = new FeatureOptions();

        // Act
        var features = options.GetNodeFeatures();

        // Assert
        Assert.True(features.IsFeatureSet(Feature.OptionDataLossProtect, false));
        Assert.False(features.IsFeatureSet(Feature.OptionDataLossProtect, true));
    }

    [Fact]
    public void Given_DefaultOptions_When_GetNodeFeatures_Then_GossipQueriesIsStillAdvertised()
    {
        // Arrange
        var options = new FeatureOptions();

        // Act
        var features = options.GetNodeFeatures();

        // Assert
        Assert.True(features.IsFeatureSet(Feature.GossipQueries, false));
    }

    [Fact]
    public void Given_DefaultOptionsOnBothSides_When_IsCompatible_Then_ReturnTrue()
    {
        // Arrange
        var local = new FeatureOptions().GetNodeFeatures();
        var remote = new FeatureOptions().GetNodeFeatures();

        // Act
        var result = local.IsCompatible(remote, out var negotiated);

        // Assert
        Assert.True(result);
        Assert.NotNull(negotiated);
        Assert.True(negotiated.IsFeatureSet(Feature.OptionDataLossProtect, false));
    }

    [Fact]
    public void Given_PeerAdvertisesOptionalFeatures_When_Negotiating_Then_OnlyFeaturesBothSupportAreNegotiated()
    {
        // Arrange
        var local = new FeatureOptions().GetNodeFeatures();
        var remote = new FeatureOptions().GetNodeFeatures();
        remote.SetFeature(Feature.OptionUpfrontShutdownScript, false);
        remote.SetFeature(Feature.OptionSupportLargeChannel, false);

        // Act
        var result = local.IsCompatible(remote, out var negotiatedFeatureSet);
        var negotiated = FeatureOptions.GetNodeOptions(negotiatedFeatureSet!, null);

        // Assert
        Assert.True(result);
        Assert.Equal(FeatureSupport.No, negotiated.UpfrontShutdownScript);
        Assert.Equal(FeatureSupport.Optional, negotiated.LargeChannels);
        Assert.Equal(FeatureSupport.No, negotiated.DualFund);
    }

    [Fact]
    public void Given_PeerRequiresFeatureWeSupportOptionally_When_Negotiating_Then_FeatureIsCompulsory()
    {
        // Arrange
        var local = new FeatureOptions { DualFund = FeatureSupport.Optional }.GetNodeFeatures();
        var remote = new FeatureOptions().GetNodeFeatures();
        remote.SetFeature(Feature.OptionDualFund, true);

        // Act
        var result = local.IsCompatible(remote, out var negotiatedFeatureSet);
        var negotiated = FeatureOptions.GetNodeOptions(negotiatedFeatureSet!, null);

        // Assert
        Assert.True(result);
        Assert.Equal(FeatureSupport.Compulsory, negotiated.DualFund);
    }

    [Fact]
    public void Given_PeerRequiresUpfrontShutdownScript_When_DefaultOptions_Then_IsNotCompatible()
    {
        // Arrange
        var local = new FeatureOptions().GetNodeFeatures();
        var remote = new FeatureOptions().GetNodeFeatures();
        remote.SetFeature(Feature.OptionUpfrontShutdownScript, true);

        // Act
        var result = local.IsCompatible(remote, out var negotiated);

        // Assert
        Assert.False(result);
        Assert.Null(negotiated);
    }
}