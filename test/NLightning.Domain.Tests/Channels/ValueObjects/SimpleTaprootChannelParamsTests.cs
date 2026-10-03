namespace NLightning.Domain.Tests.Channels.ValueObjects;

using Domain.Bitcoin.Transactions.Enums;
using Domain.Channels.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node;

/// <summary>
/// The simple taproot channel type on <see cref="ChannelParams"/> (NL-877 T3, NL-904 item 2): the flag keeps the
/// anchors semantics, gives the taproot commitment format, and describes the channel type LND accepts.
/// </summary>
public class SimpleTaprootChannelParamsTests
{
    [Fact]
    public void Given_TaprootParams_When_Read_Then_AnchorsSemanticsAndTaprootFormat()
    {
        // Arrange: even built with optionAnchorOutputs false, the taproot type keeps the anchors semantics
        var channelParams = CreateParams(3, false, FeatureSupport.No) with { OptionSimpleTaproot = true };

        // Act / Assert
        Assert.True(channelParams.OptionAnchorOutputs);
        Assert.Equal(CommitmentFormat.SimpleTaproot, channelParams.CommitmentFormat);
    }

    [Fact]
    public void Given_NonTaprootParams_When_Read_Then_FormatUnchanged()
    {
        // Act / Assert
        Assert.Equal(CommitmentFormat.Anchors, CreateParams(3, true, FeatureSupport.No).CommitmentFormat);
        Assert.Equal(CommitmentFormat.StaticRemoteKey, CreateParams(3, false, FeatureSupport.No).CommitmentFormat);
        Assert.False(CreateParams(3, true, FeatureSupport.No).OptionSimpleTaproot);
    }

    [Theory]
    [InlineData(3u, FeatureSupport.No, new[] { 80 })]
    [InlineData(0u, FeatureSupport.No, new[] { 50, 80 })]
    [InlineData(3u, FeatureSupport.Compulsory, new[] { 46, 80 })]
    [InlineData(0u, FeatureSupport.Compulsory, new[] { 46, 50, 80 })]
    public void Given_TaprootParams_When_ToChannelType_Then_Bit80WithoutStaticRemoteKeyOrAnchors(
        uint minimumDepth, FeatureSupport scidAlias, int[] expectedBits)
    {
        // Arrange
        var channelParams = CreateParams(minimumDepth, true, scidAlias) with { OptionSimpleTaproot = true };

        // Act
        var channelType = channelParams.ToChannelType();

        // Assert: exactly the sets LND 0.21 accepts ({80}, {80, 46}, {80, 50}, {80, 46, 50})
        Assert.Equal(expectedBits, channelType.GetSetBits());
        Assert.True(TaprootChannelType.IsTaprootChannelType(channelType));
    }

    [Fact]
    public void Given_TaprootParams_When_WithLocalOrWithRemote_Then_FlagKept()
    {
        // Arrange
        var channelParams = CreateParams(3, true, FeatureSupport.No) with { OptionSimpleTaproot = true };
        var party = new ChannelParty(LightningMoney.Satoshis(600), LightningMoney.Satoshis(2_000),
                                     LightningMoney.Satoshis(5), 40, LightningMoney.Satoshis(90_000), 720);

        // Act
        var withLocal = channelParams.WithLocal(party);
        var withRemote = channelParams.WithRemote(party);

        // Assert
        Assert.True(withLocal.OptionSimpleTaproot);
        Assert.True(withRemote.OptionSimpleTaproot);
        Assert.Equal(CommitmentFormat.SimpleTaproot, withRemote.CommitmentFormat);
    }

    [Fact]
    public void Given_ChannelTypes_When_IsTaprootChannelType_Then_OnlyBit80WithoutStaticRemoteKeyAndAnchors()
    {
        // Arrange
        var anchors = CreateParams(3, true, FeatureSupport.No).ToChannelType();
        var taprootWithAnchors = CreateParams(3, true, FeatureSupport.No) with { OptionSimpleTaproot = true };
        var mixed = taprootWithAnchors.ToChannelType();
        mixed.SetFeature(22, true);

        // Act / Assert
        Assert.False(TaprootChannelType.IsTaprootChannelType(null));
        Assert.False(TaprootChannelType.IsTaprootChannelType(anchors));
        Assert.False(TaprootChannelType.IsTaprootChannelType(mixed));
        Assert.Equal(CommitmentFormat.SimpleTaproot,
                     TaprootChannelType.GetCommitmentFormat(taprootWithAnchors.ToChannelType(), false));
        Assert.Equal(CommitmentFormat.Anchors, TaprootChannelType.GetCommitmentFormat(anchors, true));
    }

    private static ChannelParams CreateParams(uint minimumDepth, bool anchors, FeatureSupport useScidAlias)
    {
        var local = new ChannelParty(LightningMoney.Satoshis(354), LightningMoney.Satoshis(1_000),
                                     LightningMoney.Satoshis(1), 30, LightningMoney.Satoshis(80_000), 144);
        return new ChannelParams(local, ChannelParty.Unknown, LightningMoney.Satoshis(253), minimumDepth, anchors,
                                 useScidAlias);
    }
}