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
    [InlineData(Feature.OptionAttributionData)]
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
    public void Given_DefaultOptions_When_GetNodeFeatures_Then_GossipQueriesAndGossipQueriesExAreOptional()
    {
        // Arrange (BOLT 7 plan G3-T4: timestamps and checksums answered, proven against CLN's captured reply)
        var options = new FeatureOptions();

        // Act
        var features = options.GetNodeFeatures();

        // Assert
        Assert.True(features.IsFeatureSet(Feature.GossipQueries, false));
        Assert.False(features.IsFeatureSet(Feature.GossipQueries, true));
        Assert.True(features.IsFeatureSet(Feature.GossipQueriesEx, false));
        Assert.False(features.IsFeatureSet(Feature.GossipQueriesEx, true));
        Assert.DoesNotContain(Feature.GossipQueriesEx, FeatureOptions.ExperimentalFeatures);
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
        remote.SetFeature(Feature.OptionDualFund, false, false);

        // Act
        var result = local.IsCompatible(remote, out var negotiatedFeatureSet);
        var negotiated = FeatureOptions.GetNodeOptions(negotiatedFeatureSet!, null);

        // Assert
        Assert.True(result);
        Assert.Equal(FeatureSupport.No, negotiated.UpfrontShutdownScript);
        Assert.Equal(FeatureSupport.Optional, negotiated.LargeChannels);
        Assert.Equal(FeatureSupport.No, negotiated.DualFund);
        Assert.Equal(FeatureSupport.Optional, negotiated.OptionSplice);
        Assert.Equal(FeatureSupport.Optional, negotiated.OptionQuiesce);
    }

    [Theory]
    [InlineData(Feature.OptionQuiesce)]
    [InlineData(Feature.OptionDualFund)]
    [InlineData(Feature.OptionSplice)]
    public void Given_DefaultOptions_When_GetNodeFeatures_Then_D13FeatureIsAdvertisedOptionalAndNotExperimental(
        Feature feature)
    {
        // Arrange (splicing plan D13, wave d13: splice, quiesce and dual_fund on by default, on every network)
        var options = new FeatureOptions();

        // Act
        var init = options.GetNodeFeatures(FeatureContext.Init);
        var nodeAnnouncement = options.GetNodeFeatures(FeatureContext.NodeAnnouncement);
        var errors = options.GetValidationErrors();

        // Assert
        Assert.True(init.IsFeatureSet(feature, false));
        Assert.False(init.IsFeatureSet(feature, true));
        Assert.True(nodeAnnouncement.IsFeatureSet(feature, false));
        Assert.DoesNotContain(feature, FeatureOptions.ExperimentalFeatures);
        Assert.Empty(errors);
    }

    [Fact]
    public void Given_TwoDefaultNodes_When_Negotiating_Then_SpliceIsNegotiatedWithQuiescence()
    {
        // Arrange (D14: a splice needs both 35 and 63)
        var local = new FeatureOptions().GetNodeFeatures();
        var remote = new FeatureOptions().GetNodeFeatures();

        // Act
        var result = local.IsCompatible(remote, out var negotiatedFeatureSet);

        // Assert
        Assert.True(result);
        Assert.True(Domain.Channels.Splicing.SpliceRules.IsNegotiated(negotiatedFeatureSet!));
    }

    [Fact]
    public void Given_QuiesceTurnedOff_When_Negotiating_Then_SpliceIsNotUsable()
    {
        // Arrange: option_splice alone is advertised (BOLT 9 lists no dependency) but D14 needs 35 too
        var local = new FeatureOptions { OptionQuiesce = FeatureSupport.No }.GetNodeFeatures();
        var remote = new FeatureOptions().GetNodeFeatures();

        // Act
        var result = local.IsCompatible(remote, out var negotiatedFeatureSet);

        // Assert
        Assert.True(result);
        Assert.True(local.IsFeatureSet(Feature.OptionSplice, false));
        Assert.False(Domain.Channels.Splicing.SpliceRules.IsNegotiated(negotiatedFeatureSet!));
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
        Feature.OptionAttributionData
    ];

    [Fact]
    public void Given_DefaultOptions_When_GetNodeFeatures_Then_BasicMppIsAdvertisedOptionalAndNotExperimental()
    {
        // Arrange (ABCD W6-B: the final hop receives multi-part payments)
        var options = new FeatureOptions();

        // Act
        var features = options.GetNodeFeatures();

        // Assert
        Assert.DoesNotContain(Feature.BasicMpp, FeatureOptions.ExperimentalFeatures);
        Assert.True(features.IsFeatureSet(Feature.BasicMpp, false));
        Assert.False(features.IsFeatureSet(Feature.BasicMpp, true));
        Assert.Empty(options.GetValidationErrors());
    }

    [Fact]
    public void Given_DefaultOptions_When_GetNodeFeatures_Then_RouteBlindingIsAdvertisedOptionalAndNotExperimental()
    {
        // Arrange (ONION M5: blinded forward and receive proven against the BOLT 4 vectors and LND 0.20)
        var options = new FeatureOptions();

        // Act
        var features = options.GetNodeFeatures();

        // Assert
        Assert.DoesNotContain(Feature.OptionRouteBlinding, FeatureOptions.ExperimentalFeatures);
        Assert.True(features.IsFeatureSet(Feature.OptionRouteBlinding, false));
        Assert.False(features.IsFeatureSet(Feature.OptionRouteBlinding, true));
        Assert.Empty(options.GetValidationErrors());
    }

    [Fact]
    public void Given_DefaultOptions_When_GetNodeFeatures_Then_ProvideStorageIsAdvertisedOptionalAndNotExperimental()
    {
        // Arrange (BOLT 1 peer storage, wave rf1: the daemon registers the peer storage service)
        var options = new FeatureOptions();

        // Act
        var features = options.GetNodeFeatures();

        // Assert
        Assert.DoesNotContain(Feature.OptionProvideStorage, FeatureOptions.ExperimentalFeatures);
        Assert.True(features.IsFeatureSet(Feature.OptionProvideStorage, false));
        Assert.False(features.IsFeatureSet(Feature.OptionProvideStorage, true));
        Assert.Empty(options.GetValidationErrors());
    }

    [Fact]
    public void Given_DefaultOptions_When_GetNodeFeatures_Then_OnionMessagesIsAdvertisedOptionalAndNotExperimental()
    {
        // Arrange (BOLT 4 onion messages, wave M6: Proof M6 against CLN passed, plan D9)
        var options = new FeatureOptions();

        // Act
        var features = options.GetNodeFeatures();

        // Assert
        Assert.DoesNotContain(Feature.OptionOnionMessages, FeatureOptions.ExperimentalFeatures);
        Assert.True(features.IsFeatureSet(Feature.OptionOnionMessages, false));
        Assert.False(features.IsFeatureSet(Feature.OptionOnionMessages, true));
        Assert.Empty(options.GetValidationErrors());
    }

    [Fact]
    public void Given_BasicMppNo_When_GetNodeFeatures_Then_NotAdvertised()
    {
        // Arrange
        var options = new FeatureOptions { BasicMpp = FeatureSupport.No };

        // Act
        var features = options.GetNodeFeatures();

        // Assert
        Assert.False(features.HasFeature(Feature.BasicMpp));
    }

    [Theory]
    [MemberData(nameof(RequiredExperimentalFeatures))]
    public void Given_UnimplementedFeature_When_CheckingExperimentalFeatures_Then_ItIsExperimental(Feature feature)
    {
        // Act & Assert
        Assert.Contains(feature, FeatureOptions.ExperimentalFeatures);
    }

    [Theory]
    [MemberData(nameof(RequiredExperimentalFeatures))]
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
    public void Given_DefaultOptions_When_GetNodeFeatures_Then_AnchorsAdvertisedOptionalAndNotExperimental()
    {
        // Arrange (wave O7b, BOLT 5 plan O7-T4: option_anchors is implemented and on by default)
        var options = new FeatureOptions();

        // Act
        var initFeatures = options.GetNodeFeatures(FeatureContext.Init);
        var nodeAnnouncementFeatures = options.GetNodeFeatures(FeatureContext.NodeAnnouncement);

        // Assert
        Assert.DoesNotContain(Feature.OptionAnchors, FeatureOptions.ExperimentalFeatures);
        Assert.Equal(FeatureSupport.Optional, options.OptionAnchors);
        Assert.True(initFeatures.IsFeatureSet(Feature.OptionAnchors, false));
        Assert.False(initFeatures.IsFeatureSet(Feature.OptionAnchors, true));
        Assert.True(nodeAnnouncementFeatures.IsFeatureSet(Feature.OptionAnchors, false));
        Assert.Empty(options.GetValidationErrors());
    }

    [Fact]
    public void Given_AnchorsNo_When_GetNodeFeatures_Then_NotAdvertised()
    {
        // Arrange
        var options = new FeatureOptions { OptionAnchors = FeatureSupport.No };

        // Act
        var features = options.GetNodeFeatures();

        // Assert
        Assert.False(features.HasFeature(Feature.OptionAnchors));
        Assert.Empty(options.GetValidationErrors());
    }

    [Fact]
    public void Given_DefaultOptions_When_PeerRequiresAnchors_Then_CompatibleAndAnchorsNegotiated()
    {
        // Arrange
        var local = new FeatureOptions().GetNodeFeatures();
        var remote = new FeatureOptions { OptionAnchors = FeatureSupport.Compulsory }.GetNodeFeatures();

        // Act
        var result = local.IsCompatible(remote, out var negotiatedFeatureSet);

        // Assert
        Assert.True(result);
        Assert.Equal(FeatureSupport.Compulsory,
                     FeatureOptions.GetNodeOptions(negotiatedFeatureSet!, null).OptionAnchors);
    }

    [Fact]
    public void Given_DefaultOptions_When_PeerWithoutAnchors_Then_AnchorsNotNegotiated()
    {
        // Arrange: a peer that only knows option_static_remotekey channels
        var local = new FeatureOptions().GetNodeFeatures();
        var remote = new FeatureOptions { OptionAnchors = FeatureSupport.No }.GetNodeFeatures();

        // Act
        var result = local.IsCompatible(remote, out var negotiatedFeatureSet);

        // Assert
        Assert.True(result);
        Assert.Equal(FeatureSupport.No, FeatureOptions.GetNodeOptions(negotiatedFeatureSet!, null).OptionAnchors);
    }

    [Fact]
    public void Given_AnchorsNo_When_PeerRequiresAnchors_Then_IsNotCompatible()
    {
        // Arrange
        var local = new FeatureOptions { OptionAnchors = FeatureSupport.No }.GetNodeFeatures();
        var remote = new FeatureOptions { OptionAnchors = FeatureSupport.Compulsory }.GetNodeFeatures();

        // Act
        var result = local.IsCompatible(remote, out _);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void Given_NegotiatedExperimentalFeature_When_GetNodeOptions_Then_FeatureIsKept()
    {
        // Arrange
        var local = new FeatureOptions { AllowExperimentalFeatures = true, OptionQuiesce = FeatureSupport.Optional }
           .GetNodeFeatures();
        var remote = new FeatureOptions { AllowExperimentalFeatures = true, OptionQuiesce = FeatureSupport.Optional }
           .GetNodeFeatures();
        Assert.True(local.IsCompatible(remote, out var negotiatedFeatureSet));

        // Act
        var negotiated = FeatureOptions.GetNodeOptions(negotiatedFeatureSet!, null);

        // Assert
        // Negotiated options describe what both sides agreed on; the experimental gate only applies to what we send
        Assert.Equal(FeatureSupport.Optional, negotiated.OptionQuiesce);
        Assert.True(negotiated.GetNodeFeatures().IsFeatureSet(Feature.OptionQuiesce, false));
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
            case Feature.OptionSplice:
                options.OptionSplice = support;
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