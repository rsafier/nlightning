using Microsoft.Extensions.Logging;

namespace NLightning.Infrastructure.Tests.Node.Services;

using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Infrastructure.Node.Services;

// Covers the ping/pong and init-timeout cases of the removed Node/Models/PeerTests.cs, which moved from the Peer model
// into PeerCommunicationService. The ping-limit and ping-loop start cases live in PeerCommunicationServiceTests.
public class PeerCommunicationServiceLifecycleTests
{
    private static readonly CompactPubKey s_peerPubKey =
        Convert.FromHexString("034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa");

    private readonly Mock<IMessageService> _messageServiceMock = new();
    private readonly Mock<IMessageFactory> _messageFactoryMock = new();
    private readonly Mock<IPingPongService> _pingPongServiceMock = new();
    private readonly InitMessage _initMessage = new(new InitPayload(new FeatureSet()));

    public PeerCommunicationServiceLifecycleTests()
    {
        _messageServiceMock.SetupGet(x => x.IsConnected).Returns(true);
        _messageServiceMock.Setup(x => x.SendMessageAsync(It.IsAny<IMessage>(), It.IsAny<bool>(),
                                                          It.IsAny<CancellationToken>()))
                           .Returns(Task.CompletedTask);
        _messageFactoryMock.Setup(x => x.CreateInitMessage()).Returns(_initMessage);
        _pingPongServiceMock.Setup(x => x.StartPingAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
    }

    [Fact]
    public void Given_InitializedPeer_When_ReceivingPing_Then_SendsPong()
    {
        // Arrange
        var pingMessage = new PingMessage(new PingPayload());
        var pongMessage = new PongMessage(new PongPayload(0));
        _messageFactoryMock.Setup(x => x.CreatePongMessage(pingMessage)).Returns(pongMessage);
        using var service = CreateService();
        RaiseMessage(_initMessage);

        // Act
        RaiseMessage(pingMessage);

        // Assert
        _messageServiceMock.Verify(x => x.SendMessageAsync(pongMessage, It.IsAny<bool>(),
                                                           It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Given_UninitializedPeer_When_ReceivingPing_Then_DoesNotSendPong()
    {
        // Arrange
        var pingMessage = new PingMessage(new PingPayload());
        using var service = CreateService();

        // Act
        RaiseMessage(pingMessage);

        // Assert
        _messageFactoryMock.Verify(x => x.CreatePongMessage(It.IsAny<IMessage>()), Times.Never);
        _messageServiceMock.Verify(x => x.SendMessageAsync(It.IsAny<PongMessage>(), It.IsAny<bool>(),
                                                           It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Given_InitializedPeer_When_ReceivingPong_Then_ForwardsItToPingPongService()
    {
        // Arrange
        var pongMessage = new PongMessage(new PongPayload(0));
        using var service = CreateService();
        RaiseMessage(_initMessage);

        // Act
        RaiseMessage(pongMessage);

        // Assert
        _pingPongServiceMock.Verify(x => x.HandlePong(pongMessage), Times.Once);
    }

    [Fact]
    public async Task Given_InboundPeer_When_InitMessageIsNotReceivedWithinTimeout_Then_Disconnects()
    {
        // Arrange
        using var service = CreateService();
        var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? raised = null;
        service.ExceptionRaised += (_, e) => raised = e;
        service.DisconnectEvent += (_, e) => disconnected.TrySetResult(e);

        // Act
        await service.InitializeAsync(TimeSpan.FromMilliseconds(50));
        var completed = await Task.WhenAny(disconnected.Task,
                                           Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        // Assert
        Assert.Same(disconnected.Task, completed);
        Assert.IsType<ConnectionException>(raised);
        _messageServiceMock.Verify(x => x.SendMessageAsync(_initMessage, true, It.IsAny<CancellationToken>()),
                                   Times.Once);
    }

    [Fact]
    public async Task Given_InboundPeer_When_InitMessageIsReceivedBeforeTimeout_Then_DoesNotDisconnect()
    {
        // Arrange
        using var service = CreateService();
        var disconnected = false;
        service.DisconnectEvent += (_, _) => disconnected = true;

        // Act
        await service.InitializeAsync(TimeSpan.FromMilliseconds(100));
        RaiseMessage(_initMessage);
        await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);

        // Assert
        Assert.False(disconnected);
    }

    private PeerCommunicationService CreateService()
    {
        return new PeerCommunicationService(new Mock<ILogger<PeerCommunicationService>>().Object,
                                            _messageServiceMock.Object, _messageFactoryMock.Object, s_peerPubKey,
                                            _pingPongServiceMock.Object, new Mock<IServiceProvider>().Object);
    }

    private void RaiseMessage(IMessage message)
    {
        _messageServiceMock.Raise(x => x.OnMessageReceived += null, _messageServiceMock.Object, message);
    }
}