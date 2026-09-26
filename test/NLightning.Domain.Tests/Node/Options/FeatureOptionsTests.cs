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
        var local = new FeatureOptions { AllowExperimentalFeatures = true, DualFund = FeatureSupport.Optional }
           .GetNodeFeatures();
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

    public static TheoryData<Feature> RequiredExperimentalFeatures =>
    [
        Feature.OptionAnchors, Feature.OptionQuiesce, Feature.OptionDualFund, Feature.OptionRouteBlinding,
        Feature.OptionAttributionData, Feature.BasicMpp
    ];

    [Theory]
    [MemberData(nameof(RequiredExperimentalFeatures))]
    public void Given_UnimplementedFeature_When_CheckingExperimentalFeatures_Then_ItIsExperimental(Feature feature)
    {
        // Act & Assert
        Assert.Contains(feature, FeatureOptions.ExperimentalFeatures);
    }

    [Theory]
    [MemberData(nameof(RequiredExperimentalFeatures))]
    [InlineData(Feature.OptionOnionMessages)]
    [InlineData(Feature.OptionProvideStorage)]
    public void Given_ExperimentalFeatureEnabledWithoutOptIn_When_Validating_Then_ErrorAndNotAdvertised(
        Feature feature)
    {
        // Arrange
        var options = new FeatureOptions();
        Enable(options, feature, FeatureSupport.Optional);

        // Act
        var errors = options.GetValidationErrors();
        var features = options.GetNodeFeatures(FeatureContext.Init);
        var nodeAnnouncementFeatures = options.GetNodeFeatures(FeatureContext.NodeAnnouncement);

        // Assert
        var error = Assert.Single(errors);
        Assert.Contains(feature.ToString(), error);
        Assert.Contains(nameof(FeatureOptions.AllowExperimentalFeatures), error);
        Assert.False(features.HasFeature(feature));
        Assert.False(nodeAnnouncementFeatures.HasFeature(feature));
    }

    [Theory]
    [MemberData(nameof(RequiredExperimentalFeatures))]
    public void Given_ExperimentalFeatureEnabledWithOptIn_When_Validating_Then_NoErrorAndAdvertised(Feature feature)
    {
        // Arrange
        var options = new FeatureOptions { AllowExperimentalFeatures = true };
        Enable(options, feature, FeatureSupport.Optional);

        // Act
        var errors = options.GetValidationErrors();
        var features = options.GetNodeFeatures(FeatureContext.Init);

        // Assert
        Assert.Empty(errors);
        Assert.True(features.IsFeatureSet(feature, false));
    }

    [Fact]
    public void Given_SimpleCloseWithoutOptIn_When_Validating_Then_NoErrorAndAdvertised()
    {
        // Arrange: option_simple_close is implemented (BOLT2 plan N11), so no experimental opt-in is needed
        var options = new FeatureOptions
        {
            OptionSimpleClose = FeatureSupport.Optional,
            BeyondSegwitShutdown = FeatureSupport.Optional
        };

        // Act
        var errors = options.GetValidationErrors();
        var features = options.GetNodeFeatures(FeatureContext.Init);

        // Assert
        Assert.Empty(errors);
        Assert.DoesNotContain(Feature.OptionSimpleClose, FeatureOptions.ExperimentalFeatures);
        Assert.True(features.IsFeatureSet(Feature.OptionSimpleClose, false));
        Assert.False(new FeatureOptions().GetNodeFeatures(FeatureContext.Init)
                                         .IsFeatureSet(Feature.OptionSimpleClose, false));
    }

    [Fact]
    public void Given_CompulsoryAnchorsWithoutOptIn_When_PeerRequiresAnchors_Then_IsNotCompatible()
    {
        // Arrange
        var options = new FeatureOptions { OptionAnchors = FeatureSupport.Compulsory };
        var local = options.GetNodeFeatures();
        var remote = new FeatureOptions().GetNodeFeatures();
        remote.SetFeature(Feature.OptionAnchors, true);

        // Act
        var result = local.IsCompatible(remote, out _);

        // Assert
        // We refuse to advertise anchors, so a peer that requires them is not compatible
        Assert.False(local.HasFeature(Feature.OptionAnchors));
        Assert.False(result);
    }

    [Fact]
    public void Given_NegotiatedExperimentalFeature_When_GetNodeOptions_Then_FeatureIsKept()
    {
        // Arrange
        var local = new FeatureOptions { AllowExperimentalFeatures = true, OptionAnchors = FeatureSupport.Optional }
           .GetNodeFeatures();
        var remote = new FeatureOptions { AllowExperimentalFeatures = true, OptionAnchors = FeatureSupport.Optional }
           .GetNodeFeatures();
        Assert.True(local.IsCompatible(remote, out var negotiatedFeatureSet));

        // Act
        var negotiated = FeatureOptions.GetNodeOptions(negotiatedFeatureSet!, null);

        // Assert
        // Negotiated options describe what both sides agreed on; the experimental gate only applies to what we send
        Assert.Equal(FeatureSupport.Optional, negotiated.OptionAnchors);
        Assert.True(negotiated.GetNodeFeatures().IsFeatureSet(Feature.OptionAnchors, false));
    }

    private static void Enable(FeatureOptions options, Feature feature, FeatureSupport support)
    {
        switch (feature)
        {
            case Feature.OptionAnchors:
                options.OptionAnchors = support;
                break;
            case Feature.OptionQuiesce:
                options.OptionQuiesce = support;
                break;
            case Feature.OptionDualFund:
                options.DualFund = support;
                break;
            case Feature.OptionRouteBlinding:
                options.OptionRouteBlinding = support;
                break;
            case Feature.OptionAttributionData:
                options.OptionAttributionData = support;
                break;
            case Feature.OptionSimpleClose:
                options.OptionSimpleClose = support;
                options.BeyondSegwitShutdown = support; // BOLT 9 dependency
                break;
            case Feature.BasicMpp:
                options.BasicMpp = support;
                break;
            case Feature.OptionOnionMessages:
                options.OptionOnionMessages = support;
                break;
            case Feature.OptionProvideStorage:
                options.OptionProvideStorage = support;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(feature), feature, null);
        }
    }
}