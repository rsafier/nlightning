using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Infrastructure.Tests.Node.Services;

using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Infrastructure.Node.Services;

public class PeerCommunicationServiceTests
{
    private readonly Mock<IMessageService> _messageServiceMock = new();
    private readonly Mock<IMessageFactory> _messageFactoryMock = new();
    private readonly Mock<IPingPongService> _pingPongServiceMock = new();
    private readonly Mock<IServiceProvider> _serviceProviderMock = new();
    private readonly CompactPubKey _peerPubKey;

    public PeerCommunicationServiceTests()
    {
        var pubKey = new byte[33];
        pubKey[0] = 0x02;
        _peerPubKey = new CompactPubKey(pubKey);

        _messageServiceMock.SetupGet(x => x.IsConnected).Returns(true);
        _messageFactoryMock.Setup(x => x.CreatePongMessage(It.IsAny<IMessage>()))
                           .Returns((IMessage ping) => new PongMessage(((PingMessage)ping).Payload.NumPongBytes));
    }

    private PeerCommunicationService CreateInitializedService()
    {
        var service = new PeerCommunicationService(NullLogger<PeerCommunicationService>.Instance,
                                                   _messageServiceMock.Object, _messageFactoryMock.Object,
                                                   _peerPubKey, _pingPongServiceMock.Object,
                                                   _serviceProviderMock.Object);
        RaiseMessage(new InitMessage(new InitPayload(new FeatureSet())));
        return service;
    }

    private void RaiseMessage(IMessage message)
    {
        _messageServiceMock.Raise(x => x.OnMessageReceived += null, _messageServiceMock.Object, message);
    }

    private static PingMessage CreatePing(ushort numPongBytes)
    {
        return new PingMessage(new PingPayload { NumPongBytes = numPongBytes, BytesLength = 0, Ignored = [] });
    }

    [Theory]
    [InlineData((ushort)65532)]
    [InlineData((ushort)65535)]
    public void Given_PingWithNumPongBytesAtLeast65532_When_Received_Then_NoPongIsSent(ushort numPongBytes)
    {
        // Arrange
        _ = CreateInitializedService();

        // Act
        RaiseMessage(CreatePing(numPongBytes));

        // Assert
        _messageFactoryMock.Verify(x => x.CreatePongMessage(It.IsAny<IMessage>()), Times.Never);
        _messageServiceMock.Verify(
            x => x.SendMessageAsync(It.IsAny<PongMessage>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Given_PingPongServiceRaisesDisconnect_When_PongTimesOut_Then_PeerIsDisconnected()
    {
        // Arrange
        var service = new PeerCommunicationService(NullLogger<PeerCommunicationService>.Instance,
                                                   _messageServiceMock.Object, _messageFactoryMock.Object,
                                                   _peerPubKey, _pingPongServiceMock.Object,
                                                   _serviceProviderMock.Object);
        await service.InitializeAsync(TimeSpan.FromSeconds(30));
        RaiseMessage(new InitMessage(new InitPayload(new FeatureSet())));
        var disconnectTcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DisconnectEvent += (_, e) => disconnectTcs.TrySetResult(e);
        var timeoutException = new ConnectionException("Pong message not received within network timeout.");

        // Act
        _pingPongServiceMock.Raise(x => x.DisconnectEvent += null, _pingPongServiceMock.Object, timeoutException);
        var completed = await Task.WhenAny(disconnectTcs.Task,
                                           Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        // Assert
        Assert.Same(disconnectTcs.Task, completed);
        Assert.Same(timeoutException, await disconnectTcs.Task);
    }

    [Fact]
    public void Given_PingWithNumPongBytesBelow65532_When_Received_Then_PongWithRequestedLengthIsSent()
    {
        // Arrange
        _ = CreateInitializedService();

        // Act
        RaiseMessage(CreatePing(65531));

        // Assert
        _messageServiceMock.Verify(
            x => x.SendMessageAsync(It.Is<PongMessage>(p => p.Payload.BytesLength == 65531), It.IsAny<bool>(),
                                    It.IsAny<CancellationToken>()), Times.Once);
    }
}