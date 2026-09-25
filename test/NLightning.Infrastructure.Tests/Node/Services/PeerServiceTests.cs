using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Infrastructure.Tests.Node.Services;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;
using Infrastructure.Node.Services;

public class PeerServiceTests
{
    private readonly Mock<IPeerCommunicationService> _peerCommunicationServiceMock = new();
    private readonly FeatureOptions _features = new() { ChainHashes = [ChainConstants.Regtest] };

    public PeerServiceTests()
    {
        var pubKey = new byte[33];
        pubKey[0] = 0x02;
        _peerCommunicationServiceMock.SetupGet(x => x.PeerCompactPubKey).Returns(new CompactPubKey(pubKey));
    }

    private PeerService CreatePeerService()
    {
        return new PeerService(_peerCommunicationServiceMock.Object, _features, NullLogger<PeerService>.Instance,
                               TimeSpan.FromSeconds(1));
    }

    private void RaiseMessage(IMessage message)
    {
        _peerCommunicationServiceMock.Raise(x => x.MessageReceived += null, _peerCommunicationServiceMock.Object,
                                            message);
    }

    private InitMessage CreateInitMessage(params ChainHash[] chainHashes)
    {
        return new InitMessage(new InitPayload(_features.GetNodeFeatures()),
                               chainHashes.Length == 0 ? null : new NetworksTlv(chainHashes));
    }

    [Fact]
    public void Given_InitializedPeer_When_GossipMessageReceived_Then_MessageIsDroppedAndPeerStaysConnected()
    {
        // Arrange
        var peerService = CreatePeerService();
        RaiseMessage(CreateInitMessage(ChainConstants.Regtest));
        var channelMessageRaised = false;
        var attentionMessageRaised = false;
        peerService.OnChannelMessageReceived += (_, _) => channelMessageRaised = true;
        peerService.OnAttentionMessageReceived += (_, _) => attentionMessageRaised = true;

        // Act
        RaiseMessage(new ChannelUpdateMessage(new GossipPayload(new byte[] { 1, 2, 3 })));

        // Assert
        Assert.False(channelMessageRaised);
        Assert.False(attentionMessageRaised);
        _peerCommunicationServiceMock.Verify(x => x.Disconnect(It.IsAny<Exception?>()), Times.Never);
    }

    [Fact]
    public void Given_InitWithOurChainAmongOthers_When_InitReceived_Then_PeerIsNotDisconnected()
    {
        // Arrange
        _ = CreatePeerService();

        // Act
        RaiseMessage(CreateInitMessage(ChainConstants.Main, ChainConstants.Regtest, ChainConstants.Testnet));

        // Assert
        _peerCommunicationServiceMock.Verify(x => x.Disconnect(It.IsAny<Exception?>()), Times.Never);
    }

    [Fact]
    public void Given_InitWithNoCommonChain_When_InitReceived_Then_WarningIsSentAndPeerIsDisconnected()
    {
        // Arrange
        _ = CreatePeerService();

        // Act
        RaiseMessage(CreateInitMessage(ChainConstants.Main, ChainConstants.Testnet));

        // Assert
        _peerCommunicationServiceMock.Verify(x => x.Disconnect(It.Is<WarningException>(e => e != null)), Times.Once);
    }

    [Fact]
    public void Given_FirstMessageIsNotInit_When_MessageReceived_Then_PeerIsDisconnectedWithoutSendingAnything()
    {
        // Arrange
        _ = CreatePeerService();

        // Act
        RaiseMessage(new PingMessage());

        // Assert
        // BOLT 1: nothing may be sent before the peer's init, and ConnectionException is local only (sends nothing)
        _peerCommunicationServiceMock.Verify(x => x.Disconnect(It.IsAny<ConnectionException>()), Times.Once);
        _peerCommunicationServiceMock.Verify(x => x.Disconnect(It.IsAny<WarningException>()), Times.Never);
    }

    [Fact]
    public void Given_InitializedPeer_When_StfuReceived_Then_ChannelWarningIsSentAndPeerIsDisconnected()
    {
        // Arrange
        var channelIdBytes = new byte[32];
        channelIdBytes[31] = 0x01;
        var channelId = new ChannelId(channelIdBytes);
        _ = CreatePeerService();
        RaiseMessage(CreateInitMessage(ChainConstants.Regtest));

        // Act
        RaiseMessage(new StfuMessage(new StfuPayload(channelId, true)));

        // Assert
        // The peer now considers the channel quiescing; only a disconnection ends that (BOLT 2), so we warn on the
        // channel and disconnect, without failing the channel
        _peerCommunicationServiceMock.Verify(
            x => x.Disconnect(It.Is<ChannelWarningException>(e => e.ChannelId == channelId)), Times.Once);
        _peerCommunicationServiceMock.Verify(x => x.Disconnect(It.IsAny<ErrorException>()), Times.Never);
    }
}