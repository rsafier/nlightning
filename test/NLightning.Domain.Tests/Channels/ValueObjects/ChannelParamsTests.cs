namespace NLightning.Domain.Tests.Channels.ValueObjects;

using Domain.Channels.ValueObjects;
using Domain.Enums;
using Domain.Money;

public class ChannelParamsTests
{
    [Fact]
    public void Given_BasicParams_When_ToChannelType_Then_IsStaticRemoteKeyOnly()
    {
        // Arrange
        var channelParams = CreateParams(3, false, FeatureSupport.No);

        // Act
        var channelType = channelParams.ToChannelType();

        // Assert
        Assert.Equal(new byte[] { 0x10, 0x00 }, channelType.GetWireBytes());
    }

    [Fact]
    public void Given_ZeroConfWithoutScidAlias_When_ToChannelType_Then_OnlyZeroConfIsAdded()
    {
        // Arrange: the BOLT 9 dependency zeroconf -> scid_alias is not part of a channel type
        var channelParams = CreateParams(0, false, FeatureSupport.No);

        // Act
        var channelType = channelParams.ToChannelType();

        // Assert
        Assert.Equal([(int)Feature.OptionStaticRemoteKey - 1, (int)Feature.OptionZeroconf - 1],
                     channelType.GetSetBits());
    }

    [Fact]
    public void Given_AnchorsAndScidAlias_When_ToChannelType_Then_BothBitsAreSet()
    {
        // Arrange
        var channelParams = CreateParams(3, true, FeatureSupport.Compulsory);

        // Act
        var channelType = channelParams.ToChannelType();

        // Assert
        Assert.Equal([(int)Feature.OptionStaticRemoteKey - 1, (int)Feature.OptionAnchors - 1,
                      (int)Feature.OptionScidAlias - 1], channelType.GetSetBits());
    }

    [Fact]
    public void Given_NewRemoteParams_When_WithRemote_Then_OnlyRemoteChanges()
    {
        // Arrange
        var channelParams = CreateParams(3, false, FeatureSupport.No);
        var remote = new ChannelParty(LightningMoney.Satoshis(600), LightningMoney.Satoshis(2_000),
                                      LightningMoney.Satoshis(5), 40, LightningMoney.Satoshis(90_000), 720);

        // Act
        var updated = channelParams.WithRemote(remote);

        // Assert
        Assert.Equal(channelParams.Local, updated.Local);
        Assert.Equal(remote, updated.Remote);
        Assert.Equal(channelParams.MinimumDepth, updated.MinimumDepth);
    }

    [Fact]
    public void Given_InferredParams_When_WithRemote_Then_StillMarkedInferred()
    {
        // Arrange (a pre-NL-194 channel keeps its "inferred" mark whatever is updated)
        var channelParams = CreateParams(3, false, FeatureSupport.No) with { HasInferredParams = true };

        // Act
        var updated = channelParams.WithRemote(ChannelParty.Unknown);

        // Assert
        Assert.True(updated.HasInferredParams);
    }

    [Fact]
    public void Given_NewParams_When_Created_Then_NotMarkedInferred()
    {
        // Act
        var channelParams = CreateParams(3, false, FeatureSupport.No);

        // Assert
        Assert.False(channelParams.HasInferredParams);
    }

    private static ChannelParams CreateParams(uint minimumDepth, bool anchors, FeatureSupport useScidAlias)
    {
        var local = new ChannelParty(LightningMoney.Satoshis(354), LightningMoney.Satoshis(1_000),
                                     LightningMoney.Satoshis(1), 30, LightningMoney.Satoshis(80_000), 144);
        return new ChannelParams(local, ChannelParty.Unknown, LightningMoney.Satoshis(253), minimumDepth, anchors,
                                 useScidAlias);
    }
}