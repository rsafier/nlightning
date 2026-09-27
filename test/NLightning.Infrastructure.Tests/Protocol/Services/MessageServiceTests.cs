using Microsoft.Extensions.Logging;

namespace NLightning.Infrastructure.Tests.Protocol.Services;

using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Serialization.Interfaces;
using Domain.Transport;
using Infrastructure.Exceptions;
using Infrastructure.Protocol.Services;

public class MessageServiceTests
{
    private readonly Mock<IMessageSerializer> _messageSerializerMock;

    public MessageServiceTests()
    {
        _messageSerializerMock = new Mock<IMessageSerializer>();
    }

    [Fact]
    public async Task Given_Message_When_SendMessageAsync_IsCalled_Then_TransportServiceWritesMessage()
    {
        // Given
        var loggerMock = new Mock<ILogger<MessageService>>();
        var transportServiceMock = new Mock<ITransportService>();
        var messageService =
            new MessageService(loggerMock.Object, _messageSerializerMock.Object, transportServiceMock.Object);
        var messageMock = new Mock<IMessage>();

        // When
        await messageService.SendMessageAsync(messageMock.Object,
                                              cancellationToken: TestContext.Current.CancellationToken);

        // Then
        transportServiceMock.Verify(t => t.WriteMessageAsync(messageMock.Object, It.IsAny<CancellationToken>()),
                                    Times.Once());
    }

    [Fact]
    public void Given_ReceivedMessage_When_ReceiveMessageAsync_IsInvoked_Then_MessageReceivedEventIsRaised()
    {
        // Given
        var loggerMock = new Mock<ILogger<MessageService>>();
        var transportServiceMock = new Mock<ITransportService>();
        var messageMock = new Mock<IMessage>();
        _messageSerializerMock.Setup(m => m.DeserializeMessageAsync(It.IsAny<Stream>()))
                              .ReturnsAsync(messageMock.Object);

        var messageService =
            new MessageService(loggerMock.Object, _messageSerializerMock.Object, transportServiceMock.Object);
        var stream = new MemoryStream();

        // When & Then
        var receivedMessage = Assert.RaisesAny<IMessage?>(
            h => messageService.OnMessageReceived += h,
            h => messageService.OnMessageReceived -= h,
            () =>
            {
                // Simulate transport service receiving a message
                transportServiceMock.Raise(t => t.MessageReceived += null, messageService, stream);
            });

        Assert.NotNull(receivedMessage.Arguments);
        Assert.Same(messageMock.Object, receivedMessage.Arguments);
    }

    [Fact]
    public void Given_MalformedMessage_When_ReceiveMessageAsync_IsInvoked_Then_SendsWarningInsteadOfAllZeroError()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<MessageService>>();
        var transportServiceMock = new Mock<ITransportService>();
        transportServiceMock.Setup(t => t.IsConnected).Returns(true);
        _messageSerializerMock.Setup(m => m.DeserializeMessageAsync(It.IsAny<Stream>()))
                              .ThrowsAsync(new MessageSerializationException("bad message"));
        IMessage? sentMessage = null;
        transportServiceMock.Setup(t => t.WriteMessageAsync(It.IsAny<IMessage>(), It.IsAny<CancellationToken>()))
                            .Callback<IMessage, CancellationToken>((m, _) => sentMessage = m)
                            .Returns(Task.CompletedTask);
        var messageService =
            new MessageService(loggerMock.Object, _messageSerializerMock.Object, transportServiceMock.Object);
        messageService.OnMessageReceived += (_, _) => { };
        Exception? raisedException = null;
        messageService.OnExceptionRaised += (_, e) => raisedException = e;

        // Act
        transportServiceMock.Raise(t => t.MessageReceived += null, messageService, new MemoryStream());

        // Assert
        var warning = Assert.IsType<WarningMessage>(sentMessage);
        Assert.Equal(MessageTypes.Warning, warning.Type);
        Assert.Equal(ChannelId.Zero, warning.Payload.ChannelId);
        Assert.NotNull(raisedException);
    }

    [Fact]
    public void Given_UnknownEvenMessageType_When_Received_Then_SendsWarningAndRaisesConnectionException()
    {
        // Arrange (BOLT 1: an unknown even message closes the connection; we send a warning first)
        var transportServiceMock = new Mock<ITransportService>();
        transportServiceMock.Setup(t => t.IsConnected).Returns(true);
        _messageSerializerMock.Setup(m => m.DeserializeMessageAsync(It.IsAny<Stream>()))
                              .ThrowsAsync(new InvalidMessageException("Unknown message type 100"));
        IMessage? sentMessage = null;
        transportServiceMock.Setup(t => t.WriteMessageAsync(It.IsAny<IMessage>(), It.IsAny<CancellationToken>()))
                            .Callback<IMessage, CancellationToken>((m, _) => sentMessage = m)
                            .Returns(Task.CompletedTask);
        var messageService = new MessageService(new Mock<ILogger<MessageService>>().Object,
                                                _messageSerializerMock.Object, transportServiceMock.Object);
        messageService.OnMessageReceived += (_, _) => { };
        Exception? raisedException = null;
        messageService.OnExceptionRaised += (_, e) => raisedException = e;

        // Act
        transportServiceMock.Raise(t => t.MessageReceived += null, messageService, new MemoryStream());

        // Assert
        var warning = Assert.IsType<WarningMessage>(sentMessage);
        Assert.Equal(ChannelId.Zero, warning.Payload.ChannelId);
        Assert.Equal("Unknown message type 100", System.Text.Encoding.UTF8.GetString(warning.Payload.Data!));
        Assert.IsType<ConnectionException>(raisedException);
    }

    [Theory]
    [InlineData(MessageTypes.ChannelAnnouncement)]
    [InlineData(MessageTypes.NodeAnnouncement)]
    [InlineData(MessageTypes.ChannelUpdate)]
    public void Given_MalformedGossipBroadcast_When_Received_Then_IgnoredWithOneWarningAndConnectionKept(
        MessageTypes type)
    {
        // Arrange (mainnet gossip probe: LND relays pre-2022 channel_updates without htlc_maximum_msat, 128 bytes)
        var transportServiceMock = new Mock<ITransportService>();
        transportServiceMock.Setup(t => t.IsConnected).Returns(true);
        _messageSerializerMock.Setup(m => m.DeserializeMessageAsync(It.IsAny<Stream>()))
                              .ThrowsAsync(new MessageSerializationException(
                                               "Error deserializing message",
                                               new PayloadSerializationException(
                                                   "Error deserializing ChannelUpdatePayload",
                                                   new InvalidOperationException(
                                                       "A channel_update payload is at least 136 bytes, got 128"))));
        var sentMessages = new List<IMessage>();
        transportServiceMock.Setup(t => t.WriteMessageAsync(It.IsAny<IMessage>(), It.IsAny<CancellationToken>()))
                            .Callback<IMessage, CancellationToken>((m, _) => sentMessages.Add(m))
                            .Returns(Task.CompletedTask);
        var messageService = new MessageService(new Mock<ILogger<MessageService>>().Object,
                                                _messageSerializerMock.Object, transportServiceMock.Object);
        messageService.OnMessageReceived += (_, _) => { };
        Exception? raisedException = null;
        messageService.OnExceptionRaised += (_, e) => raisedException = e;
        var bytes = new byte[2 + 128];
        bytes[0] = (byte)((ushort)type >> 8);
        bytes[1] = (byte)(ushort)type;

        // Act
        transportServiceMock.Raise(t => t.MessageReceived += null, messageService, new MemoryStream(bytes));
        transportServiceMock.Raise(t => t.MessageReceived += null, messageService, new MemoryStream(bytes));

        // Assert: one connection-level warning for the first, nothing for the second, and the connection stays
        var warning = Assert.IsType<WarningMessage>(Assert.Single(sentMessages));
        Assert.Equal(ChannelId.Zero, warning.Payload.ChannelId);
        Assert.Contains("136 bytes, got 128", System.Text.Encoding.UTF8.GetString(warning.Payload.Data!));
        Assert.Null(raisedException);
    }

    [Fact]
    public void Given_MalformedOnionMessage_When_Received_Then_IgnoredWithoutWarningCountedAndConnectionKept()
    {
        // Arrange: NL-444, a 513 whose len is below 66 fails in the payload serializer; BOLT 4 ignores an unusable
        // onion message and 513 is odd, so no warning and no close, only the dropped{reason=malformed} count
        var transportServiceMock = new Mock<ITransportService>();
        transportServiceMock.Setup(t => t.IsConnected).Returns(true);
        _messageSerializerMock.Setup(m => m.DeserializeMessageAsync(It.IsAny<Stream>()))
                              .ThrowsAsync(new MessageSerializationException(
                                               "Error deserializing message",
                                               new PayloadSerializationException(
                                                   "Error deserializing OnionMessagePayload",
                                                   new System.Runtime.Serialization.SerializationException(
                                                       "onion_message_packet len 10 is below 66"))));
        var messageService = new MessageService(new Mock<ILogger<MessageService>>().Object,
                                                _messageSerializerMock.Object, transportServiceMock.Object);
        messageService.OnMessageReceived += (_, _) => { };
        Exception? raisedException = null;
        messageService.OnExceptionRaised += (_, e) => raisedException = e;
        long malformedDrops = 0;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "NLightning.OnionMessages"
             && instrument.Name == "nlightning.onion_messages.dropped")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag is { Key: "reason", Value: MessageService.MalformedOnionMessageDropReason })
                    Interlocked.Add(ref malformedDrops, value);
        });
        listener.Start();
        var bytes = new byte[] { 0x02, 0x01, 0x00, 0x0a };

        // Act
        transportServiceMock.Raise(t => t.MessageReceived += null, messageService, new MemoryStream(bytes));
        transportServiceMock.Raise(t => t.MessageReceived += null, messageService, new MemoryStream(bytes));

        // Assert
        transportServiceMock.Verify(t => t.WriteMessageAsync(It.IsAny<IMessage>(), It.IsAny<CancellationToken>()),
                                    Times.Never());
        Assert.Null(raisedException);
        Assert.True(Interlocked.Read(ref malformedDrops) >= 2);
    }

    [Fact]
    public void Given_MalformedChannelMessage_When_Received_Then_StillWarnsAndCloses()
    {
        // Arrange: only the gossip broadcasts are ignored; a malformed update_add_htlc (128) still closes
        var transportServiceMock = new Mock<ITransportService>();
        transportServiceMock.Setup(t => t.IsConnected).Returns(true);
        _messageSerializerMock.Setup(m => m.DeserializeMessageAsync(It.IsAny<Stream>()))
                              .ThrowsAsync(new MessageSerializationException("bad message"));
        transportServiceMock.Setup(t => t.WriteMessageAsync(It.IsAny<IMessage>(), It.IsAny<CancellationToken>()))
                            .Returns(Task.CompletedTask);
        var messageService = new MessageService(new Mock<ILogger<MessageService>>().Object,
                                                _messageSerializerMock.Object, transportServiceMock.Object);
        messageService.OnMessageReceived += (_, _) => { };
        Exception? raisedException = null;
        messageService.OnExceptionRaised += (_, e) => raisedException = e;

        // Act
        transportServiceMock.Raise(t => t.MessageReceived += null, messageService,
                                   new MemoryStream([0x00, (byte)MessageTypes.UpdateAddHtlc, 0x01]));

        // Assert
        Assert.IsType<ConnectionException>(raisedException);
    }

    [Fact]
    public void Given_SubscriberThrows_When_MessageReceived_Then_NoWarningIsSent()
    {
        // Arrange (only deserialization failures are the peer's fault)
        var transportServiceMock = new Mock<ITransportService>();
        _messageSerializerMock.Setup(m => m.DeserializeMessageAsync(It.IsAny<Stream>()))
                              .ReturnsAsync(new Mock<IMessage>().Object);
        var messageService = new MessageService(new Mock<ILogger<MessageService>>().Object,
                                                _messageSerializerMock.Object, transportServiceMock.Object);
        messageService.OnMessageReceived += (_, _) => throw new InvalidOperationException("subscriber failed");
        Exception? raisedException = null;
        messageService.OnExceptionRaised += (_, e) => raisedException = e;

        // Act
        transportServiceMock.Raise(t => t.MessageReceived += null, messageService, new MemoryStream());

        // Assert
        transportServiceMock.Verify(t => t.WriteMessageAsync(It.IsAny<IMessage>(), It.IsAny<CancellationToken>()),
                                    Times.Never);
        Assert.IsType<ConnectionException>(raisedException);
    }

    [Fact]
    public void Given_NoSubscriber_When_Constructed_Then_DoesNotListenToTheTransport()
    {
        // Arrange - NL-239: listening (which starts the transport's read loop) before anyone subscribed here raised
        // the peer's init to nobody
        var transportServiceMock = new Mock<ITransportService>();
        var subscriptions = 0;
        transportServiceMock.SetupAdd(t => t.MessageReceived += It.IsAny<EventHandler<MemoryStream>>())
                            .Callback(() => subscriptions++);

        // Act
        using var messageService = new MessageService(new Mock<ILogger<MessageService>>().Object,
                                                      _messageSerializerMock.Object, transportServiceMock.Object);
        var beforeSubscriber = subscriptions;
        messageService.OnMessageReceived += (_, _) => { };
        messageService.OnMessageReceived += (_, _) => { };

        // Assert
        Assert.Equal(0, beforeSubscriber);
        Assert.Equal(1, subscriptions);
    }

    [Fact]
    public void Given_TransportServiceConnectionState_When_CheckingIsConnected_Then_ReturnsCorrectValue()
    {
        // Given
        var loggerMock = new Mock<ILogger<MessageService>>();
        var transportServiceMock = new Mock<ITransportService>();
        transportServiceMock.Setup(t => t.IsConnected).Returns(true);
        var messageService =
            new MessageService(loggerMock.Object, _messageSerializerMock.Object, transportServiceMock.Object);

        // When & Then
        Assert.True(messageService.IsConnected);
    }

    [Fact]
    public void Given_MessageService_When_Dispose_IsCalled_Then_TransportServiceIsDisposed()
    {
        // Given
        var loggerMock = new Mock<ILogger<MessageService>>();
        var transportServiceMock = new Mock<ITransportService>();
        var messageService =
            new MessageService(loggerMock.Object, _messageSerializerMock.Object, transportServiceMock.Object);

        // When
        messageService.Dispose();

        // Then
        transportServiceMock.Verify(t => t.Dispose(), Times.Once());
    }

    [Fact]
    public async Task Given_DisposedMessageService_When_SendMessageAsync_IsCalled_Then_ThrowsConnectionException()
    {
        // Given
        var loggerMock = new Mock<ILogger<MessageService>>();
        var transportServiceMock = new Mock<ITransportService>();
        var messageService =
            new MessageService(loggerMock.Object, _messageSerializerMock.Object, transportServiceMock.Object);
        var messageMock = new Mock<IMessage>();

        // When
        messageService.Dispose();

        // Then
        var exception = await Assert.ThrowsAsync<ConnectionException>(() => messageService.SendMessageAsync(
                                                                          messageMock.Object, true,
                                                                          TestContext.Current.CancellationToken));
        Assert.IsType<ObjectDisposedException>(exception.InnerException);
    }
}