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
    public async Task Given_ReceivedMessage_When_ReceiveMessageAsync_IsInvoked_Then_MessageReceivedEventIsRaised()
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

        // When: the transport's raw frame is queued and deserialized by the per-peer consumer (NL-108), so the
        // event arrives shortly after the raise
        var received = new TaskCompletionSource<IMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        messageService.OnMessageReceived += (_, m) => received.TrySetResult(m);
        transportServiceMock.Raise(t => t.MessageReceived += null, messageService, stream);

        // Then
        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Same(messageMock.Object, message);
    }

    [Fact]
    public async Task Given_MalformedMessage_When_ReceiveMessageAsync_IsInvoked_Then_SendsWarningInsteadOfAllZeroError()
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
        await WaitUntilAsync(() => sentMessage is not null && raisedException is not null,
                             "the warning and the exception", TestContext.Current.CancellationToken);
        var warning = Assert.IsType<WarningMessage>(sentMessage);
        Assert.Equal(MessageTypes.Warning, warning.Type);
        Assert.Equal(ChannelId.Zero, warning.Payload.ChannelId);
    }

    [Fact]
    public async Task Given_UnknownEvenMessageType_When_Received_Then_SendsWarningAndRaisesConnectionException()
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
        await WaitUntilAsync(() => sentMessage is not null && raisedException is not null,
                             "the warning and the exception", TestContext.Current.CancellationToken);
        var warning = Assert.IsType<WarningMessage>(sentMessage);
        Assert.Equal(ChannelId.Zero, warning.Payload.ChannelId);
        Assert.Equal("Unknown message type 100", System.Text.Encoding.UTF8.GetString(warning.Payload.Data!));
        Assert.IsType<ConnectionException>(raisedException);
    }

    [Theory]
    [InlineData(MessageTypes.ChannelAnnouncement)]
    [InlineData(MessageTypes.NodeAnnouncement)]
    [InlineData(MessageTypes.ChannelUpdate)]
    public async Task Given_MalformedGossipBroadcast_When_Received_Then_IgnoredWithOneWarningAndConnectionKept(
        MessageTypes type)
    {
        // Arrange (mainnet gossip probe: LND relays pre-2022 channel_updates without htlc_maximum_msat, 128 bytes)
        var transportServiceMock = new Mock<ITransportService>();
        transportServiceMock.Setup(t => t.IsConnected).Returns(true);
        var validMessage = new Mock<IMessage>();
        var deserializeCalls = 0;
        _messageSerializerMock
            .Setup(m => m.DeserializeMessageAsync(It.IsAny<Stream>()))
            .Returns((Stream _) =>
            {
                // The first two frames are the malformed gossip broadcasts, the third a valid message: the consumer
                // is strictly in order, so its dispatch proves both malformed frames were fully handled
                var call = Interlocked.Increment(ref deserializeCalls);
                return call <= 2
                    ? Task.FromException<IMessage?>(new MessageSerializationException(
                          "Error deserializing message",
                          new PayloadSerializationException(
                              "Error deserializing ChannelUpdatePayload",
                              new InvalidOperationException(
                                  "A channel_update payload is at least 136 bytes, got 128"))))
                    : Task.FromResult<IMessage?>(validMessage.Object);
            });
        var sentMessages = new List<IMessage>();
        transportServiceMock.Setup(t => t.WriteMessageAsync(It.IsAny<IMessage>(), It.IsAny<CancellationToken>()))
                            .Callback<IMessage, CancellationToken>((m, _) => sentMessages.Add(m))
                            .Returns(Task.CompletedTask);
        var messageService = new MessageService(new Mock<ILogger<MessageService>>().Object,
                                                _messageSerializerMock.Object, transportServiceMock.Object);
        var validDispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        messageService.OnMessageReceived += (_, m) =>
        {
            if (m == validMessage.Object)
                validDispatched.TrySetResult();
        };
        Exception? raisedException = null;
        messageService.OnExceptionRaised += (_, e) => raisedException = e;
        var bytes = new byte[2 + 128];
        bytes[0] = (byte)((ushort)type >> 8);
        bytes[1] = (byte)(ushort)type;

        // Act
        transportServiceMock.Raise(t => t.MessageReceived += null, messageService, new MemoryStream(bytes));
        transportServiceMock.Raise(t => t.MessageReceived += null, messageService, new MemoryStream(bytes));
        transportServiceMock.Raise(t => t.MessageReceived += null, messageService, new MemoryStream([0x00, 0x00]));

        // Assert: one connection-level warning for the first, nothing for the second, and the connection stays
        await validDispatched.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var warning = Assert.IsType<WarningMessage>(Assert.Single(sentMessages));
        Assert.Equal(ChannelId.Zero, warning.Payload.ChannelId);
        Assert.Contains("136 bytes, got 128", System.Text.Encoding.UTF8.GetString(warning.Payload.Data!));
        Assert.Null(raisedException);
    }

    [Fact]
    public async Task Given_MalformedOnionMessage_When_Received_Then_IgnoredWithoutWarningCountedAndConnectionKept()
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
        await WaitUntilAsync(() => Interlocked.Read(ref malformedDrops) >= 2, "both drops to be counted",
                             TestContext.Current.CancellationToken);
        transportServiceMock.Verify(t => t.WriteMessageAsync(It.IsAny<IMessage>(), It.IsAny<CancellationToken>()),
                                    Times.Never());
        Assert.Null(raisedException);
    }

    [Fact]
    public async Task Given_MalformedOnionMessageAndADropCounter_When_Received_Then_CountedOnTheCounterNotTheStaticMeter()
    {
        // Arrange: NL-464, a node with the onion-message counter set (the service's) counts the malformed 513s on it,
        // so one meter and one in-memory count hold every drop; the class's own meter stays unused
        var transportServiceMock = new Mock<ITransportService>();
        transportServiceMock.Setup(t => t.IsConnected).Returns(true);
        _messageSerializerMock.Setup(m => m.DeserializeMessageAsync(It.IsAny<Stream>()))
                              .ThrowsAsync(new MessageSerializationException(
                                               "Error deserializing message",
                                               new PayloadSerializationException(
                                                   "Error deserializing OnionMessagePayload",
                                                   new System.Runtime.Serialization.SerializationException(
                                                       "onion_message_packet len 10 is below 66"))));
        var drops = new List<string>();
        var counter = new Mock<Domain.Protocol.OnionMessages.Interfaces.IOnionMessageDropCounter>();
        counter.Setup(c => c.RecordDropped(It.IsAny<string>()))
               .Callback<string>(reason => drops.Add(reason));
        var messageService = new MessageService(new Mock<ILogger<MessageService>>().Object,
                                                _messageSerializerMock.Object, transportServiceMock.Object,
                                                counter.Object);
        messageService.OnMessageReceived += (_, _) => { };
        long staticMeterDrops = 0;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "NLightning.OnionMessages"
             && instrument.Name == "nlightning.onion_messages.dropped")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) => Interlocked.Add(ref staticMeterDrops, value));
        listener.Start();
        var bytes = new byte[] { 0x02, 0x01, 0x00, 0x0a };

        // Act
        transportServiceMock.Raise(t => t.MessageReceived += null, messageService, new MemoryStream(bytes));
        transportServiceMock.Raise(t => t.MessageReceived += null, messageService, new MemoryStream(bytes));

        // Assert: the counter took both, the static fallback meter nothing
        await WaitUntilAsync(() => drops.Count >= 2, "both drops to be counted", TestContext.Current.CancellationToken);
        counter.Verify(c => c.RecordDropped(MessageService.MalformedOnionMessageDropReason), Times.Exactly(2));
        Assert.Equal(0, Interlocked.Read(ref staticMeterDrops));
    }

    [Fact]
    public async Task Given_MalformedChannelMessage_When_Received_Then_StillWarnsAndCloses()
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
        await WaitUntilAsync(() => raisedException is not null, "the connection exception",
                             TestContext.Current.CancellationToken);
        Assert.IsType<ConnectionException>(raisedException);
    }

    [Fact]
    public async Task Given_SubscriberThrows_When_MessageReceived_Then_NoWarningIsSent()
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
        await WaitUntilAsync(() => raisedException is not null, "the connection exception",
                             TestContext.Current.CancellationToken);
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

    [Fact]
    public async Task Given_SlowSubscriber_When_TransportRaisesMoreFrames_Then_TheRaiseNeverWaitsAndOrderIsKept()
    {
        // Arrange - NL-108: the read loop (here the transport raise) only queues, so a slow handler grows the queue
        // instead of stalling the reads, and the frames are dispatched in arrival order afterwards
        var transportServiceMock = new Mock<ITransportService>();
        var messages = Enumerable.Range(0, 64).Select(_ => new Mock<IMessage>().Object).ToList();
        var next = 0;
        _messageSerializerMock.Setup(m => m.DeserializeMessageAsync(It.IsAny<Stream>()))
                              .ReturnsAsync((Stream _) => messages[next++]);
        var messageService = new MessageService(new Mock<ILogger<MessageService>>().Object,
                                                _messageSerializerMock.Object, transportServiceMock.Object);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new List<IMessage>();
        var handled = 0;
        messageService.OnMessageReceived += (_, m) =>
        {
            lock (delivered)
                delivered.Add(m!);

            // The first "handler" is the slow one: it parks the consumer until the test releases it
            if (Interlocked.Increment(ref handled) == 1)
            {
                firstEntered.TrySetResult();
                releaseHandler.Task.Wait(TestContext.Current.CancellationToken);
            }
        };

        // Act: the "read loop" raises every frame while the handler of the first one is still stuck
        var readLoop = Task.Run(() =>
        {
            foreach (var _ in messages)
                transportServiceMock.Raise(t => t.MessageReceived += null, messageService, new MemoryStream());
        }, TestContext.Current.CancellationToken);

        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var raiseTask = await Task.WhenAny(readLoop,
                                           Task.Delay(TimeSpan.FromSeconds(10),
                                                      TestContext.Current.CancellationToken));

        // Assert: the raises all returned while the slow handler had only delivered the first message (the queue
        // holds the rest), and once released everything arrives, in order
        Assert.Same(readLoop, raiseTask);
        Assert.False(readLoop.IsFaulted);
        Assert.False(releaseHandler.Task.IsCompleted);
        lock (delivered)
            Assert.Single(delivered);

        releaseHandler.TrySetResult();
        await readLoop.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() =>
        {
            lock (delivered)
                return delivered.Count == messages.Count;
        }, "every message to be delivered", TestContext.Current.CancellationToken);
        lock (delivered)
            Assert.Equal(messages, delivered);
    }

    /// <summary>
    /// Waits for a condition the per-peer consumer reaches asynchronously (a warning being sent, a counter moving).
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string what, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for {what}");

            await Task.Delay(10, cancellationToken);
        }
    }
}