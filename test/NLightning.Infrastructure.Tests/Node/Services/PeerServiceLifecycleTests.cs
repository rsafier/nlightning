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
    public void Given_InitializationFails_When_Constructing_Then_ThrowsConnectionExceptionAndClosesTheConnection()
    {
        // Arrange - NL-240: the half-set-up connection stayed open, so the other end kept a dead connection
        _communicationMock.Setup(x => x.InitializeAsync(It.IsAny<TimeSpan>()))
                          .ThrowsAsync(new ConnectionException("Failed to connect to peer"));

        // Act & Assert
        var exception = Assert.Throws<ConnectionException>(() => CreatePeerService());
        Assert.IsType<ConnectionException>(exception.InnerException);
        _communicationMock.Verify(x => x.Dispose(), Times.Once);
    }

    [Fact]
    public async Task Given_PeerSendsCompatibleInit_When_WaitingForInit_Then_Completes()
    {
        // Arrange
        using var peerService = CreatePeerService();
        var wait = peerService.WaitForInitAsync(TestContext.Current.CancellationToken);
        Assert.False(wait.IsCompleted);

        // Act
        RaiseMessage(new InitMessage(new InitPayload(_features.GetNodeFeatures())));

        // Assert
        await wait.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_ConnectionClosesBeforeInit_When_WaitingForInit_Then_ThrowsConnectionException()
    {
        // Arrange
        using var peerService = CreatePeerService();
        var wait = peerService.WaitForInitAsync(TestContext.Current.CancellationToken);

        // Act
        _communicationMock.Raise(x => x.DisconnectEvent += null, _communicationMock.Object,
                                 new ConnectionException("closed"));

        // Assert
        await Assert.ThrowsAsync<ConnectionException>(() => wait.WaitAsync(TimeSpan.FromSeconds(5),
                                                                           TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_FirstMessageIsNotInit_When_WaitingForInit_Then_ThrowsConnectionException()
    {
        // Arrange - the peer service disconnects; the communication service then raises its disconnect event
        _communicationMock.Setup(x => x.Disconnect(It.IsAny<Exception?>()))
                          .Callback((Exception? e) => _communicationMock.Raise(x => x.DisconnectEvent += null,
                                                                               _communicationMock.Object, e!));
        using var peerService = CreatePeerService();
        var wait = peerService.WaitForInitAsync(TestContext.Current.CancellationToken);

        // Act
        RaiseMessage(CreateChannelMessage());

        // Assert
        await Assert.ThrowsAsync<ConnectionException>(() => wait.WaitAsync(TimeSpan.FromSeconds(5),
                                                                           TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Given_Constructing_When_SubscribingToCommunication_Then_MessagesAreSubscribedLast()
    {
        // Arrange - subscribing to MessageReceived starts reading (NL-239); a bad first message disconnects at once,
        // so the disconnect handler must already be in place
        var order = new List<string>();
        _communicationMock.SetupAdd(x => x.DisconnectEvent += It.IsAny<EventHandler<Exception?>>())
                          .Callback(() => order.Add("disconnect"));
        _communicationMock.SetupAdd(x => x.MessageReceived += It.IsAny<EventHandler<IMessage?>>())
                          .Callback(() => order.Add("message"));

        // Act
        using var peerService = CreatePeerService();

        // Assert
        Assert.Equal(["disconnect", "message"], order);
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

    [Fact]
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

    [Fact]
    public void Given_ChannelMessagesBeforeAnySubscriber_When_Subscribing_Then_TheyAreDeliveredInOrder()
    {
        // Arrange (the read loop runs before PeerManager subscribes; LND sends channel_reestablish right after init)
        using var peerService = CreatePeerService();
        RaiseMessage(CreateInitMessage(_features.GetNodeFeatures(), new NetworksTlv([ChainConstants.Regtest])));
        var first = CreateChannelMessage();
        var second = CreateChannelMessage();
        RaiseMessage(first);
        RaiseMessage(second);
        var received = new List<IChannelMessage>();

        // Act
        peerService.OnChannelMessageReceived += (_, args) => received.Add(args.Message);
        var third = CreateChannelMessage();
        RaiseMessage(third);

        // Assert
        Assert.Equal(new IChannelMessage[] { first, second, third }, received);
        _communicationMock.Verify(x => x.Disconnect(It.IsAny<Exception?>()), Times.Never);
    }

    [Fact]
    public void Given_TooManyChannelMessagesBeforeAnySubscriber_When_Receiving_Then_Disconnects()
    {
        // Arrange
        using var peerService = CreatePeerService();
        RaiseMessage(CreateInitMessage(_features.GetNodeFeatures(), new NetworksTlv([ChainConstants.Regtest])));

        // Act
        for (var i = 0; i <= PeerService.MaxPendingChannelMessages; i++)
            RaiseMessage(CreateChannelMessage());

        // Assert
        _communicationMock.Verify(x => x.Disconnect(It.IsAny<ConnectionException>()), Times.Once);
    }

    [Fact]
    public void Given_AlreadyDisconnected_When_SubscribingToOnDisconnect_Then_HandlerIsCalledRightAway()
    {
        // Arrange (a peer that drops before PeerManager subscribes must not stay in the peer table)
        using var peerService = CreatePeerService();
        _communicationMock.Raise(x => x.DisconnectEvent += null, _communicationMock.Object,
                                 new ConnectionException("gone"));
        var calls = 0;

        // Act
        peerService.OnDisconnect += (_, _) => calls++;

        // Assert
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Given_Subscribed_When_DisconnectEventRaisedTwice_Then_OnDisconnectIsRaisedOnce()
    {
        // Arrange
        using var peerService = CreatePeerService();
        var calls = 0;
        peerService.OnDisconnect += (_, _) => calls++;

        // Act
        _communicationMock.Raise(x => x.DisconnectEvent += null, _communicationMock.Object,
                                 new ConnectionException("gone"));
        _communicationMock.Raise(x => x.DisconnectEvent += null, _communicationMock.Object,
                                 new ConnectionException("gone again"));

        // Assert
        Assert.Equal(1, calls);
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