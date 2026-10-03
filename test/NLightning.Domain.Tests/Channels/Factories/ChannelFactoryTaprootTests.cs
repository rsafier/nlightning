using NLightning.Tests.Utils.Mocks;

namespace NLightning.Domain.Tests.Channels.Factories;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Channels.Factories;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;

/// <summary>
/// Simple taproot channels in the channel factory (NL-877 T5): our <c>openchannel --channel-type taproot</c> and the
/// peer's taproot <c>open_channel</c> make a channel with <see cref="ChannelParams.OptionSimpleTaproot"/>, the anchors
/// semantics and the MuSig2 funding output; our open is refused unless our node advertises the feature and the peer
/// negotiated it and <c>option_simple_close</c>, and for a public channel.
/// </summary>
public class ChannelFactoryTaprootTests
{
    private static readonly CompactPubKey s_remoteNodeId =
        Convert.FromHexString("034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa");

    private static readonly ChannelId s_temporaryChannelId =
        Convert.FromHexString("0101010101010101010101010101010101010101010101010101010101010101");

    private static FeatureOptions Negotiated => new()
    {
        OptionSimpleTaproot = FeatureSupport.Optional,
        OptionSimpleClose = FeatureSupport.Optional,
        OptionAnchors = FeatureSupport.Optional,
        AllowExperimentalFeatures = true
    };

    private static NodeOptions Advertising => new()
    {
        MinimumChannelSize = LightningMoney.Satoshis(1_000),
        Features = new FeatureOptions
        {
            OptionSimpleTaproot = FeatureSupport.Optional,
            AllowExperimentalFeatures = true
        }
    };

    [Fact]
    public async Task Given_ATaprootRequest_When_CreatingChannelAsInitiator_Then_TheChannelIsSimpleTaproot()
    {
        // Act
        var channel = await CreateFactory(Advertising).CreateChannelV1AsInitiatorAsync(Request(), Negotiated,
                                                                                         s_remoteNodeId);

        // Assert
        Assert.True(channel.ChannelParams.OptionSimpleTaproot);
        Assert.True(channel.ChannelParams.OptionAnchorOutputs);
        Assert.Equal(CommitmentFormat.SimpleTaproot, channel.ChannelParams.CommitmentFormat);
        Assert.Equal([TaprootChannelType.CompulsoryBit], channel.ChannelParams.ToChannelType().GetSetBits());
        Assert.False(channel.AnnounceChannel);
    }

    [Fact]
    public async Task Given_OurNodeDoesNotAdvertiseTaproot_When_CreatingChannelAsInitiator_Then_Refused()
    {
        // Configured but without AllowExperimentalFeatures: the feature is not advertised
        var options = new NodeOptions
        {
            MinimumChannelSize = LightningMoney.Satoshis(1_000),
            Features = new FeatureOptions { OptionSimpleTaproot = FeatureSupport.Optional }
        };

        var exception = await Assert.ThrowsAsync<ChannelErrorException>(
                            () => CreateFactory(options).CreateChannelV1AsInitiatorAsync(Request(), Negotiated,
                                                                                         s_remoteNodeId));
        Assert.Contains("AllowExperimentalFeatures", exception.Message);
    }

    [Theory]
    [InlineData(false, true, "does not support simple taproot")]
    [InlineData(true, false, "option_simple_close")]
    public async Task Given_APeerWithoutTaprootOrSimpleClose_When_CreatingChannelAsInitiator_Then_Refused(
        bool taproot, bool simpleClose, string reason)
    {
        var negotiated = new FeatureOptions
        {
            OptionSimpleTaproot = taproot ? FeatureSupport.Optional : FeatureSupport.No,
            OptionSimpleClose = simpleClose ? FeatureSupport.Optional : FeatureSupport.No,
            AllowExperimentalFeatures = true
        };

        var exception = await Assert.ThrowsAsync<ChannelErrorException>(
                            () => CreateFactory(Advertising).CreateChannelV1AsInitiatorAsync(Request(), negotiated,
                                                                                             s_remoteNodeId));
        Assert.Contains(reason, exception.Message);
    }

    [Fact]
    public async Task Given_APublicTaprootRequest_When_CreatingChannelAsInitiator_Then_Refused()
    {
        var request = Request();
        request.IsPublic = true;

        var exception = await Assert.ThrowsAsync<ChannelErrorException>(
                            () => CreateFactory(Advertising).CreateChannelV1AsInitiatorAsync(request, Negotiated,
                                                                                             s_remoteNodeId));
        Assert.Contains("private", exception.Message);
    }

    [Fact]
    public async Task Given_ATaprootOpenChannel_When_CreatingChannelAsNonInitiator_Then_TheFundingOutputIsP2Tr()
    {
        // Arrange
        var channelType = FeatureSet.DeserializeFromBytes([]);
        channelType.SetFeature(TaprootChannelType.CompulsoryBit, true);
        var payload = new OpenChannel1Payload(BitcoinNetwork.Mainnet.ChainHash, new ChannelFlags(ChannelFlag.None),
                                              s_temporaryChannelId, LightningMoney.Satoshis(1_000), s_remoteNodeId,
                                              LightningMoney.Satoshis(354), LightningMoney.Satoshis(1_000),
                                              s_remoteNodeId, LightningMoney.Satoshis(100_000), s_remoteNodeId,
                                              s_remoteNodeId, LightningMoney.Satoshis(1), 30,
                                              LightningMoney.Satoshis(100_000), s_remoteNodeId, LightningMoney.Zero,
                                              s_remoteNodeId, 144);
        var message = new OpenChannel1Message(payload, new ChannelTypeTlv(channelType));

        // Act
        var channel = await CreateFactory(Advertising).CreateChannelV1AsNonInitiatorAsync(message, Negotiated,
                                                                                           s_remoteNodeId);

        // Assert
        Assert.True(channel.ChannelParams.OptionSimpleTaproot);
        Assert.True(channel.FundingOutput!.IsSimpleTaproot);
    }

    private static ChannelFactory CreateFactory(NodeOptions nodeOptions)
    {
        var signerMock = new Mock<ILightningSigner>();
        var basepoints = new ChannelBasepoints(s_remoteNodeId, s_remoteNodeId, s_remoteNodeId, s_remoteNodeId,
                                               s_remoteNodeId);
        var firstPerCommitmentPoint = s_remoteNodeId;
        signerMock.Setup(s => s.CreateNewChannel(out basepoints, out firstPerCommitmentPoint)).Returns(0);

        var feeServiceMock = new Mock<IFeeService>();
        feeServiceMock.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()))
                      .ReturnsAsync(LightningMoney.Satoshis(1_000));

        return new ChannelFactory(new Mock<IChannelIdFactory>().Object, new Mock<IChannelOpenValidator>().Object,
                                  feeServiceMock.Object, signerMock.Object, nodeOptions, new FakeSha256());
    }

    private static OpenChannelClientRequest Request() =>
        new("node", LightningMoney.Satoshis(200_000))
        {
            FeeRatePerKw = LightningMoney.Satoshis(2_500),
            ChannelReserveAmount = LightningMoney.Satoshis(2_000),
            IsSimpleTaproot = true
        };
}