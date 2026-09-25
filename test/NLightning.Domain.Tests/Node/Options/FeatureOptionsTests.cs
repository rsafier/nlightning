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
}