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
    public void Given_InitializedPeer_When_StfuReceived_Then_ChannelWarningIsSentAndPeerStaysConnected()
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
        _peerCommunicationServiceMock.Verify(
            x => x.SendWarningAsync(It.Is<ChannelWarningException>(e => e.ChannelId == channelId),
                                    It.IsAny<CancellationToken>()), Times.Once);
        _peerCommunicationServiceMock.Verify(x => x.Disconnect(It.IsAny<Exception?>()), Times.Never);
    }
}