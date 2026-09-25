namespace NLightning.Domain.Tests.Node;

using Domain.Node;
using Domain.Protocol.Tlv;
using Enums;

public class FeatureSetTests
{
    #region SetFeature IsFeatureSet

    [Theory]
    [InlineData(Feature.OptionDataLossProtect, false)]
    [InlineData(Feature.OptionDataLossProtect, true)]
    [InlineData(Feature.OptionUpfrontShutdownScript, false)]
    [InlineData(Feature.OptionUpfrontShutdownScript, true)]
    [InlineData(Feature.OptionSupportLargeChannel, false)]
    [InlineData(Feature.OptionSupportLargeChannel, true)]
    public void Given_Features_When_SetFeatureA_Then_OnlyFeatureAIsSet(Feature feature, bool isCompulsory)
    {
        // Arrange
        var features = new FeatureSet();
        var eventRaised = false;
        features.Changed += (_, _) => eventRaised = true;

        // Act
        features.SetFeature(feature, isCompulsory);

        // Assert
        Assert.True(features.IsFeatureSet(feature, isCompulsory));
        Assert.False(features.IsFeatureSet(feature, !isCompulsory));
        Assert.False(features.IsFeatureSet(Feature.OptionPaymentMetadata, isCompulsory));
        Assert.False(features.IsFeatureSet(Feature.OptionPaymentMetadata, !isCompulsory));
        Assert.True(eventRaised);
    }

    [Theory]
    [InlineData(Feature.OptionDataLossProtect, false)]
    [InlineData(Feature.OptionDataLossProtect, true)]
    [InlineData(Feature.OptionUpfrontShutdownScript, false)]
    [InlineData(Feature.OptionUpfrontShutdownScript, true)]
    [InlineData(Feature.OptionSupportLargeChannel, false)]
    [InlineData(Feature.OptionSupportLargeChannel, true)]
    public void Given_Features_When_UnsetFeatureA_Then_FeatureBIsSet(Feature feature, bool isCompulsory)
    {
        // Arrange
        var features = new FeatureSet();
        var eventRaised = false;
        features.Changed += (_, _) => eventRaised = true;
        features.SetFeature(Feature.OptionPaymentMetadata, isCompulsory);
        features.SetFeature(feature, isCompulsory);

        // Act
        features.SetFeature(feature, isCompulsory, false);

        // Assert
        Assert.False(features.IsFeatureSet(feature, isCompulsory));
        Assert.False(features.IsFeatureSet(feature, !isCompulsory));
        Assert.True(features.IsFeatureSet(Feature.OptionPaymentMetadata, isCompulsory));
        Assert.False(features.IsFeatureSet(Feature.OptionPaymentMetadata, !isCompulsory));
        Assert.True(eventRaised);
    }

    [Theory]
    [InlineData(Feature.OptionZeroconf, Feature.OptionScidAlias, false)]
    [InlineData(Feature.OptionZeroconf, Feature.OptionScidAlias, true)]
    [InlineData(Feature.OptionSimpleClose, Feature.OptionShutdownAnySegwit, false)]
    [InlineData(Feature.OptionSimpleClose, Feature.OptionShutdownAnySegwit, true)]
    [InlineData(Feature.OptionOnionMessagesOnlyChannels, Feature.OptionOnionMessages, false)]
    [InlineData(Feature.OptionOnionMessagesOnlyChannels, Feature.OptionOnionMessages, true)]
    public void Given_Features_When_SetFeatureADependsOnFeatureB_Then_FeatureBIsSet(
        Feature feature, Feature dependsOn, bool isCompulsory)
    {
        // Arrange
        var features = new FeatureSet();
        var eventRaised = false;
        features.Changed += (_, _) => eventRaised = true;

        // Act
        features.SetFeature(feature, isCompulsory);

        // Assert
        Assert.True(features.IsFeatureSet(feature, isCompulsory));
        Assert.True(features.IsFeatureSet(dependsOn, isCompulsory));
        Assert.True(eventRaised);
    }

    [Theory]
    [InlineData(Feature.OptionScidAlias, Feature.OptionZeroconf, false)]
    [InlineData(Feature.OptionScidAlias, Feature.OptionZeroconf, true)]
    [InlineData(Feature.OptionShutdownAnySegwit, Feature.OptionSimpleClose, false)]
    [InlineData(Feature.OptionShutdownAnySegwit, Feature.OptionSimpleClose, true)]
    public void Given_Features_When_UnsetFeatureA_Then_FeatureBIsUnset(Feature feature, Feature dependent,
                                                                       bool isCompulsory)
    {
        // Arrange
        var features = new FeatureSet();
        var eventRaised = false;
        features.Changed += (_, _) => eventRaised = true;
        features.SetFeature(dependent, isCompulsory);

        // Act
        features.SetFeature(feature, isCompulsory, false);

        // Assert
        Assert.False(features.IsFeatureSet(feature, isCompulsory));
        Assert.False(features.IsFeatureSet(dependent, isCompulsory));
        Assert.True(eventRaised);
    }

    [Fact]
    public void Given_Features_When_SetUnknownFeature_Then_UnknownFeatureIsSet()
    {
        // Arrange
        var features = new FeatureSet();
        var eventRaised = false;
        features.Changed += (_, _) => eventRaised = true;

        // Act
        features.SetFeature(134, true);

        // Assert
        Assert.True(features.IsFeatureSet(134, false));
        Assert.True(eventRaised);
    }

    #endregion

    #region IsCompatible

    [Theory]
    [InlineData(Feature.OptionDataLossProtect, false, false, false, false, true)]
    [InlineData(Feature.OptionDataLossProtect, false, true, false, false, true)]
    [InlineData(Feature.OptionDataLossProtect, false, true, false, true, true)]
    [InlineData(Feature.OptionDataLossProtect, false, false, false, true, true)]
    [InlineData(Feature.OptionDataLossProtect, true, false, false, false, true)]
    [InlineData(Feature.OptionDataLossProtect, false, false, true, false, true)]
    [InlineData(Feature.OptionDataLossProtect, true, false, true, false, true)]
    [InlineData(Feature.OptionDataLossProtect, false, true, true, false, false)]
    [InlineData(Feature.OptionDataLossProtect, true, false, false, true, false)]
    public void Given_Features_When_IsCompatible_Then_ReturnIsKnown(Feature feature, bool unsetLocal,
                                                                    bool isLocalCompulsorySet, bool unsetOther,
                                                                    bool isOtherCompulsorySet, bool expected)
    {
        // Arrange
        var features = new FeatureSet();
        var other = new FeatureSet();
        if (unsetLocal)
        {
            features.SetFeature(feature, isLocalCompulsorySet, false);
            features.SetFeature(feature, !isLocalCompulsorySet, false);
            other.SetFeature(feature, isOtherCompulsorySet);
        }
        else if (unsetOther)
        {
            other.SetFeature(feature, isOtherCompulsorySet, false);
            other.SetFeature(feature, !isOtherCompulsorySet, false);
            features.SetFeature(feature, isLocalCompulsorySet);
        }
        else
        {
            features.SetFeature(feature, isLocalCompulsorySet);
            other.SetFeature(feature, isOtherCompulsorySet);
        }

        // Act
        var result = features.IsCompatible(other, out var _);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Given_Features_When_OtherDontSupportVarOnionOptin_Then_ReturnFalse()
    {
        // Arrange
        var features = new FeatureSet();
        var other = new FeatureSet();

        other.SetFeature(Feature.VarOnionOptin, true, false);
        other.SetFeature(Feature.VarOnionOptin, false, false);

        // Act
        var result = features.IsCompatible(other, out var _);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void Given_Features_When_OtherFeatureHasUnknownOptionalFeatureSet_Then_ReturnTrue()
    {
        // Arrange
        var features = new FeatureSet();
        var other = new FeatureSet();
        features.SetFeature(Feature.OptionDataLossProtect, false);
        other.SetFeature(41, true);

        // Act
        var result = features.IsCompatible(other, out var _);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void Given_Features_When_OtherFeatureHasUnknownCompulsoryFeatureSet_Then_ReturnFalse()
    {
        // Arrange
        var features = new FeatureSet();
        var other = new FeatureSet();
        features.SetFeature(Feature.OptionDataLossProtect, false);
        other.SetFeature(42, true);

        // Act
        var result = features.IsCompatible(other, out var _);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void Given_Features_When_OtherFeatureDontSetDependency_Then_ReturnFalse()
    {
        // Arrange
        var features = new FeatureSet();
        features.SetFeature(Feature.OptionZeroconf, false);
        var other = new FeatureSet();
        other.SetFeature(Feature.OptionZeroconf, false);
        other.SetFeature((int)Feature.OptionScidAlias, false);

        // Act
        var result = features.IsCompatible(other, out var _);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void Given_OptionalFeatureWithCompulsoryDependency_When_SetFeature_Then_DependencyStaysCompulsory()
    {
        // Arrange
        var features = new FeatureSet();

        // Act
        features.SetFeature(Feature.BasicMpp, false);

        // Assert
        Assert.True(features.IsFeatureSet(Feature.BasicMpp, false));
        Assert.True(features.IsFeatureSet(Feature.PaymentSecret, true));
        Assert.False(features.IsFeatureSet(Feature.PaymentSecret, false));
    }

    [Theory]
    [InlineData(Feature.BasicMpp, Feature.PaymentSecret)]
    [InlineData(Feature.ZeroFeeCommitments, Feature.OptionChannelType)]
    [InlineData(Feature.OptionSimpleClose, Feature.OptionShutdownAnySegwit)]
    [InlineData(Feature.OptionOnionMessagesOnlyChannels, Feature.OptionOnionMessages)]
    public void Given_OtherSetsFeatureWithoutDependency_When_IsCompatible_Then_ReturnFalse(Feature feature,
        Feature dependency)
    {
        // Arrange
        var features = new FeatureSet();
        var other = new FeatureSet();
        other.SetFeature(dependency, true, false);
        other.SetFeature((int)feature, true);

        // Act
        var result = features.IsCompatible(other, out var negotiated);

        // Assert
        Assert.False(result);
        Assert.Null(negotiated);
    }

    [Fact]
    public void Given_OtherSetsGossipQueriesExWithoutGossipQueries_When_IsCompatible_Then_ReturnTrue()
    {
        // Arrange
        var features = new FeatureSet();
        var other = new FeatureSet();
        other.SetFeature((int)Feature.GossipQueriesEx, true);

        // Act
        var result = features.IsCompatible(other, out _);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void Given_LocalSetMissingDependency_When_IsCompatible_Then_ReturnFalse()
    {
        // Arrange
        var features = new FeatureSet();
        features.SetFeature((int)Feature.OptionZeroconf, true);
        var other = new FeatureSet();
        other.SetFeature(Feature.OptionZeroconf, false);

        // Act
        var result = features.IsCompatible(other, out var negotiated);

        // Assert
        Assert.False(result);
        Assert.Null(negotiated);
        Assert.Contains((Feature.OptionZeroconf, Feature.OptionScidAlias), features.GetMissingDependencies());
    }

    [Fact]
    public void Given_FeatureOnlyLocallyOptional_When_IsCompatible_Then_FeatureIsNotNegotiated()
    {
        // Arrange
        var features = new FeatureSet();
        features.SetFeature(Feature.OptionDataLossProtect, false);
        var other = new FeatureSet();
        other.SetFeature(Feature.OptionDataLossProtect, false, false);

        // Act
        var result = features.IsCompatible(other, out var negotiated);

        // Assert
        Assert.True(result);
        Assert.NotNull(negotiated);
        Assert.False(negotiated.HasFeature(Feature.OptionDataLossProtect));
        Assert.True(negotiated.IsFeatureSet(Feature.VarOnionOptin, true));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Given_BothSupportFeature_When_IsCompatible_Then_FeatureIsNegotiated(bool isLocalCompulsory,
        bool isOtherCompulsory, bool expectedCompulsory)
    {
        // Arrange
        var features = new FeatureSet();
        features.SetFeature(Feature.OptionSupportLargeChannel, isLocalCompulsory);
        var other = new FeatureSet();
        other.SetFeature(Feature.OptionSupportLargeChannel, isOtherCompulsory);

        // Act
        var result = features.IsCompatible(other, out var negotiated);

        // Assert
        Assert.True(result);
        Assert.NotNull(negotiated);
        Assert.True(negotiated.IsFeatureSet(Feature.OptionSupportLargeChannel, expectedCompulsory));
        Assert.False(negotiated.IsFeatureSet(Feature.OptionSupportLargeChannel, !expectedCompulsory));
    }

    #endregion

    #region FilterByContext

    [Fact]
    public void Given_Features_When_FilterByInitContext_Then_OnlyInitFeaturesAreKept()
    {
        // Arrange
        var features = new FeatureSet();
        features.SetFeature(Feature.OptionPaymentMetadata, false);
        features.SetFeature(Feature.OptionUpfrontShutdownScript, false);
        features.SetFeature(101, true);

        // Act
        var filtered = features.FilterByContext(FeatureContext.Init);

        // Assert
        Assert.False(filtered.HasFeature(Feature.OptionPaymentMetadata));
        Assert.False(filtered.IsFeatureSet(101, false));
        Assert.True(filtered.IsFeatureSet(Feature.OptionUpfrontShutdownScript, false));
        Assert.True(filtered.IsFeatureSet(Feature.VarOnionOptin, true));
        Assert.True(features.HasFeature(Feature.OptionPaymentMetadata));
    }

    [Fact]
    public void Given_Features_When_FilterByInvoiceContext_Then_OnlyInvoiceFeaturesAreKept()
    {
        // Arrange
        var features = new FeatureSet();
        features.SetFeature(Feature.OptionPaymentMetadata, false);
        features.SetFeature(Feature.BasicMpp, false);
        features.SetFeature(Feature.OptionUpfrontShutdownScript, false);

        // Act
        var filtered = features.FilterByContext(FeatureContext.Invoice);

        // Assert
        Assert.True(filtered.IsFeatureSet(Feature.OptionPaymentMetadata, false));
        Assert.True(filtered.IsFeatureSet(Feature.BasicMpp, false));
        Assert.True(filtered.IsFeatureSet(Feature.PaymentSecret, true));
        Assert.True(filtered.IsFeatureSet(Feature.VarOnionOptin, true));
        Assert.False(filtered.HasFeature(Feature.OptionUpfrontShutdownScript));
        Assert.False(filtered.HasFeature(Feature.OptionDataLossProtect));
        Assert.False(filtered.HasFeature(Feature.OptionChannelType));
    }

    [Fact]
    public void Given_EveryKnownFeature_When_GetContexts_Then_ContextIsDefined()
    {
        foreach (var feature in Enum.GetValues<Feature>())
            Assert.NotEqual(FeatureContext.None, FeatureSet.GetContexts(feature));
    }

    #endregion

    #region Combine

    [Fact]
    public void Given_Features_When_Combine_Then_FeaturesAreCombined()
    {
        // Arrange
        var global = new FeatureSet();
        global.SetFeature(Feature.OptionDataLossProtect, true);
        global.SetFeature(Feature.OptionUpfrontShutdownScript, false);
        global.SetFeature(Feature.GossipQueries, true);

        var features = new FeatureSet();
        features.SetFeature(Feature.OptionUpfrontShutdownScript, true);
        features.SetFeature(Feature.GossipQueries, false);
        features.SetFeature(Feature.OptionSupportLargeChannel, true);

        // Act
        var combined = FeatureSet.Combine(global, features);

        // Assert
        Assert.True(combined.IsFeatureSet(Feature.OptionDataLossProtect, true));
        Assert.True(combined.IsFeatureSet(Feature.OptionUpfrontShutdownScript, true));
        Assert.True(combined.IsFeatureSet(Feature.OptionSupportLargeChannel, true));
        Assert.True(combined.IsFeatureSet(Feature.GossipQueries, true));
    }

    #endregion

    #region DeserializeFromBytes

    [Fact]
    public void Given_ByteArray_When_DeserializeFromBytes_Then_InputIsNotMutated()
    {
        // Arrange
        byte[] data = [0x40, 0x10, 0x00];
        byte[] expected = [0x40, 0x10, 0x00];

        // Act
        var features = FeatureSet.DeserializeFromBytes(data);

        // Assert
        Assert.Equal(expected, data);
        Assert.True(features.IsFeatureSet(Feature.OptionStaticRemoteKey, true));
        Assert.True(features.IsFeatureSet(Feature.OptionAnchors, true));
    }

    [Fact]
    public void Given_MultiByteChannelType_When_CreatingChannelTypeTlv_Then_ValueKeepsWireOrder()
    {
        // Arrange
        byte[] channelType = [0x40, 0x10, 0x00];
        byte[] expected = [0x40, 0x10, 0x00];

        // Act
        var tlv = new ChannelTypeTlv(channelType);

        // Assert
        Assert.Equal(expected, tlv.ChannelType);
        Assert.Equal(expected, tlv.Value);
        Assert.True(tlv.Features.IsFeatureSet(Feature.OptionStaticRemoteKey, true));
        Assert.True(tlv.Features.IsFeatureSet(Feature.OptionAnchors, true));
    }

    [Fact]
    public void Given_BasicChannelType_When_CreatingChannelTypeTlvFromFeatureSet_Then_ValueIsBigEndian()
    {
        // Arrange
        var channelType = FeatureSet.NewBasicChannelType();
        byte[] expected = [0x10, 0x00];

        // Act
        var tlv = new ChannelTypeTlv(channelType);

        // Assert
        Assert.Equal(expected, channelType.GetWireBytes());
        Assert.Equal(expected, tlv.Value);
        Assert.True(tlv.Features.IsFeatureSet(Feature.OptionStaticRemoteKey, true));
        Assert.False(tlv.Features.HasFeature(Feature.OptionUpfrontShutdownScript));
    }

    [Fact]
    public void Given_AnchorsChannelType_When_GettingWireBytes_Then_RoundTripsThroughDeserialize()
    {
        // Arrange
        var channelType = FeatureSet.NewBasicChannelType();
        channelType.SetFeature(Feature.OptionAnchors, true);
        byte[] expected = [0x40, 0x10, 0x00];

        // Act
        var wireBytes = channelType.GetWireBytes();
        var roundTripped = FeatureSet.DeserializeFromBytes(wireBytes!);

        // Assert
        Assert.Equal(expected, wireBytes);
        Assert.True(roundTripped.IsFeatureSet(Feature.OptionStaticRemoteKey, true));
        Assert.True(roundTripped.IsFeatureSet(Feature.OptionAnchors, true));
    }

    #endregion
}