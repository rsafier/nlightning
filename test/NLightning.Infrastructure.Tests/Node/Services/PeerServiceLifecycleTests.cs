using Microsoft.Extensions.Logging;

namespace NLightning.Infrastructure.Tests.Node.Services;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node;
using Domain.Node.Events;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Infrastructure.Node.Services;

// Revived from the removed Node/Models/PeerTests.cs (the Peer model was replaced by PeerService).
// Protocol-level cases (gossip, stfu, networks) live in PeerServiceTests.
public class PeerServiceLifecycleTests
{
    private static readonly CompactPubKey s_peerPubKey =
        Convert.FromHexString("034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa");

    private readonly Mock<IPeerCommunicationService> _communicationMock = new();
    private readonly Mock<ILogger<PeerService>> _loggerMock = new();
    private readonly FeatureOptions _features = new() { ChainHashes = [ChainConstants.Regtest] };

    public PeerServiceLifecycleTests()
    {
        _communicationMock.SetupGet(x => x.PeerCompactPubKey).Returns(s_peerPubKey);
        _communicationMock.Setup(x => x.InitializeAsync(It.IsAny<TimeSpan>())).Returns(Task.CompletedTask);
    }

    [Fact]
    public void Given_PeerCommunicationService_When_Constructing_Then_InitializesCommunication()
    {
        // Arrange
        var timeout = TimeSpan.FromSeconds(5);

        // Act
        using var peerService = CreatePeerService(timeout);

        // Assert
        _communicationMock.Verify(x => x.InitializeAsync(timeout), Times.Once);
        Assert.Equal(s_peerPubKey, peerService.PeerPubKey);
    }

    [Fact]
    public void Given_InitializationFails_When_Constructing_Then_ThrowsErrorException()
    {
        // Arrange
        _communicationMock.Setup(x => x.InitializeAsync(It.IsAny<TimeSpan>()))
                          .ThrowsAsync(new ConnectionException("Failed to connect to peer"));

        // Act & Assert
        var exception = Assert.Throws<ErrorException>(() => CreatePeerService());
        Assert.IsType<ConnectionException>(exception.InnerException);
    }

    [Fact]
    public void Given_UninitializedPeer_When_FirstMessageIsNotInit_Then_Disconnects()
    {
        // Arrange
        using var peerService = CreatePeerService();

        // Act
        RaiseMessage(CreateChannelMessage());

        // Assert
        _communicationMock.Verify(x => x.Disconnect(It.IsAny<Exception?>()), Times.Once);
    }

    [Fact]
    public void Given_UninitializedPeer_When_ReceivingCompatibleInit_Then_ChannelMessagesAreForwarded()
    {
        // Arrange
        using var peerService = CreatePeerService();
        ChannelMessageEventArgs? received = null;
        peerService.OnChannelMessageReceived += (_, args) => received = args;
        var channelMessage = CreateChannelMessage();

        // Act
        RaiseMessage(CreateInitMessage(_features.GetNodeFeatures(), new NetworksTlv([ChainConstants.Regtest])));
        RaiseMessage(channelMessage);

        // Assert
        _communicationMock.Verify(x => x.Disconnect(It.IsAny<Exception?>()), Times.Never);
        Assert.NotNull(received);
        Assert.Same(channelMessage, received.Message);
        Assert.Equal(s_peerPubKey, received.PeerPubKey);
    }

    [Fact]
    public void Given_UninitializedPeer_When_ReceivingIncompatibleFeatures_Then_Disconnects()
    {
        // Arrange - BOLT 9: an unknown compulsory (even) feature bit must fail the connection
        using var peerService = CreatePeerService();
        var remoteFeatures = new FeatureSet();
        remoteFeatures.SetFeature(100, true);

        // Act
        RaiseMessage(CreateInitMessage(remoteFeatures, null));

        // Assert
        _communicationMock.Verify(x => x.Disconnect(It.IsAny<Exception?>()), Times.Once);
    }

    [Fact]
    public void Given_UninitializedPeer_When_ReceivingIncompatibleChain_Then_Disconnects()
    {
        // Arrange
        using var peerService = CreatePeerService();

        // Act
        RaiseMessage(CreateInitMessage(_features.GetNodeFeatures(), new NetworksTlv([ChainConstants.Main])));

        // Assert
        _communicationMock.Verify(x => x.Disconnect(It.IsAny<Exception?>()), Times.Once);
    }

    [Fact(Skip = "NL-002: init disconnects when any remote chain is unknown instead of only when none is shared")]
    public void Given_UninitializedPeer_When_ReceivingInitWithASharedChain_Then_DoesNotDisconnect()
    {
        // Arrange - BOLT 1: only disconnect if the peer and we share no chain in `networks`
        using var peerService = CreatePeerService();

        // Act
        RaiseMessage(CreateInitMessage(_features.GetNodeFeatures(),
                                       new NetworksTlv([ChainConstants.Regtest, ChainConstants.Main])));

        // Assert
        _communicationMock.Verify(x => x.Disconnect(It.IsAny<Exception?>()), Times.Never);
    }

    [Fact]
    public void Given_InitializedPeer_When_ReceivingErrorMessage_Then_RaisesAttentionEvent()
    {
        // Arrange
        using var peerService = CreatePeerService();
        AttentionMessageEventArgs? received = null;
        peerService.OnAttentionMessageReceived += (_, args) => received = args;
        RaiseMessage(CreateInitMessage(_features.GetNodeFeatures(), new NetworksTlv([ChainConstants.Regtest])));

        // Act
        RaiseMessage(new ErrorMessage(new ErrorPayload("something went wrong")));

        // Assert
        Assert.NotNull(received);
        Assert.Equal("something went wrong", received.Message);
        Assert.Null(received.ChannelId);
    }

    [Fact]
    public void Given_PeerService_When_CommunicationDisconnects_Then_RaisesOnDisconnect()
    {
        // Arrange
        using var peerService = CreatePeerService();
        PeerDisconnectedEventArgs? received = null;
        peerService.OnDisconnect += (_, args) => received = args;

        // Act
        _communicationMock.Raise(x => x.DisconnectEvent += null, _communicationMock.Object,
                                 new ConnectionException("gone"));

        // Assert
        Assert.NotNull(received);
    }

    private PeerService CreatePeerService(TimeSpan? timeout = null)
    {
        return new PeerService(_communicationMock.Object, _features, _loggerMock.Object,
                               timeout ?? TimeSpan.FromSeconds(1));
    }

    private void RaiseMessage(IMessage message)
    {
        _communicationMock.Raise(x => x.MessageReceived += null, _communicationMock.Object, message);
    }

    private static InitMessage CreateInitMessage(FeatureSet featureSet, NetworksTlv? networksTlv)
    {
        return new InitMessage(new InitPayload(featureSet), networksTlv);
    }

    private static ChannelReadyMessage CreateChannelMessage()
    {
        return new ChannelReadyMessage(new ChannelReadyPayload(ChannelId.Zero, s_peerPubKey));
    }
}