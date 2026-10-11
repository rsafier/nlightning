using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Tests.Node.Services;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Gossip.Addresses;
using Domain.Node;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Domain.Serialization.Interfaces;
using Domain.Transport;
using Infrastructure.Node.Services;
using Infrastructure.Protocol.Services;

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
        Listen(service);
        RaiseMessage(new InitMessage(new InitPayload(new FeatureSet())));
        return service;
    }

    [Fact]
    public async Task Given_AnInboundRemoteAddress_When_Initializing_Then_TheInitCarriesItAsRemoteAddr()
    {
        // Arrange - NL-009, BOLT 1: the receiver of an IP connection sends the endpoint the peer connected from
        var remoteAddress = new AddressDescriptor(AddressDescriptorType.IPv4, [203, 0, 113, 7], 9735);
        var initMessage = new InitMessage(new InitPayload(new FeatureSet()),
                                          remoteAddressTlv: new RemoteAddressTlv(remoteAddress));
        _messageFactoryMock.Setup(x => x.CreateInitMessage(remoteAddress)).Returns(initMessage);
        var service = new PeerCommunicationService(NullLogger<PeerCommunicationService>.Instance,
                                                   _messageServiceMock.Object, _messageFactoryMock.Object,
                                                   _peerPubKey, _pingPongServiceMock.Object,
                                                   _serviceProviderMock.Object, remoteAddress);
        Listen(service);

        // Act
        await service.InitializeAsync(TimeSpan.FromSeconds(30));

        // Assert
        _messageFactoryMock.Verify(x => x.CreateInitMessage(remoteAddress), Times.Once);
        _messageServiceMock.Verify(x => x.SendMessageAsync(initMessage, It.IsAny<bool>(),
                                                           It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_NoRemoteAddress_When_Initializing_Then_NoRemoteAddrIsAskedFor()
    {
        // Arrange - BOLT 1: only the receiver of an IP connection sets remote_addr, so an outbound one sends none
        var initMessage = new InitMessage(new InitPayload(new FeatureSet()));
        _messageFactoryMock.Setup(x => x.CreateInitMessage(null)).Returns(initMessage);
        var service = new PeerCommunicationService(NullLogger<PeerCommunicationService>.Instance,
                                                   _messageServiceMock.Object, _messageFactoryMock.Object,
                                                   _peerPubKey, _pingPongServiceMock.Object,
                                                   _serviceProviderMock.Object);
        Listen(service);

        // Act
        await service.InitializeAsync(TimeSpan.FromSeconds(30));

        // Assert
        _messageFactoryMock.Verify(x => x.CreateInitMessage(null), Times.Once);
        _messageServiceMock.Verify(x => x.SendMessageAsync(initMessage, It.IsAny<bool>(),
                                                           It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The service only listens to the message service once it has a subscriber itself (NL-239).
    /// </summary>
    private static void Listen(PeerCommunicationService service)
    {
        service.MessageReceived += (_, _) => { };
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

    [Fact]
    public void Given_PingsUpToTheAnswerLimit_When_Received_Then_EachGetsAPong()
    {
        // Arrange
        _ = CreateInitializedService();

        // Act
        for (var i = 0; i < PingRateLimiter.DefaultMaxAnsweredPings; i++)
            RaiseMessage(CreatePing(32));

        // Assert
        _messageFactoryMock.Verify(x => x.CreatePongMessage(It.IsAny<IMessage>()),
                                   Times.Exactly(PingRateLimiter.DefaultMaxAnsweredPings));
        _messageServiceMock.Verify(
            x => x.SendMessageAsync(It.IsAny<PongMessage>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Exactly(PingRateLimiter.DefaultMaxAnsweredPings));
    }

    [Fact]
    public void Given_APingFlood_When_Received_Then_ThePingsBeyondTheAnswerLimitGetNoPong()
    {
        // Arrange (NL-005, BOLT 1: limited precautions against ping flooding)
        _ = CreateInitializedService();

        // Act
        for (var i = 0; i < PingRateLimiter.DefaultMaxAnsweredPings + 3; i++)
            RaiseMessage(CreatePing(32));

        // Assert - the first pings keep their pong, the flood beyond the limit is ignored without one
        _messageFactoryMock.Verify(x => x.CreatePongMessage(It.IsAny<IMessage>()),
                                   Times.Exactly(PingRateLimiter.DefaultMaxAnsweredPings));
        _messageServiceMock.Verify(
            x => x.SendMessageAsync(It.IsAny<PongMessage>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Exactly(PingRateLimiter.DefaultMaxAnsweredPings));
    }

    [Fact]
    public async Task Given_RealPingPongService_When_PeerInitArrivesAfterInitialize_Then_PeerIsNotDisconnected()
    {
        // Arrange
        var networkTimeout = TimeSpan.FromMilliseconds(300);
        _messageFactoryMock.Setup(x => x.CreatePingMessage()).Returns(() => new PingMessage());
        _messageFactoryMock.Setup(x => x.CreateInitMessage())
                           .Returns(new InitMessage(new InitPayload(new FeatureSet())));
        SetupUnitOfWork();
        var pingPongService = new PingPongService(_messageFactoryMock.Object,
                                                  Options.Create(new NodeOptions { NetworkTimeout = networkTimeout }));
        var service = new PeerCommunicationService(NullLogger<PeerCommunicationService>.Instance,
                                                   _messageServiceMock.Object, _messageFactoryMock.Object,
                                                   _peerPubKey, pingPongService, _serviceProviderMock.Object);
        Listen(service);

        // The peer answers every ping we send with a matching pong
        _messageServiceMock
           .Setup(x => x.SendMessageAsync(It.IsAny<PingMessage>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
           .Callback((IMessage ping, bool _, CancellationToken _) =>
                         _ = Task.Run(() => RaiseMessage(
                                          new PongMessage(((PingMessage)ping).Payload.NumPongBytes))))
           .Returns(Task.CompletedTask);

        var disconnectTcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DisconnectEvent += (_, e) => disconnectTcs.TrySetResult(e);

        // Act
        await service.InitializeAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(20, TestContext.Current.CancellationToken);
        RaiseMessage(new InitMessage(new InitPayload(new FeatureSet())));
        var completed = await Task.WhenAny(disconnectTcs.Task,
                                           Task.Delay(networkTimeout * 4, TestContext.Current.CancellationToken));

        // Assert
        Assert.NotSame(disconnectTcs.Task, completed);
        _messageServiceMock.Verify(
            x => x.SendMessageAsync(It.IsAny<PingMessage>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Once);

        service.Disconnect();
    }

    [Fact]
    public async Task Given_PeerInitNotReceived_When_Initialized_Then_NoPingIsStarted()
    {
        // Arrange
        _messageFactoryMock.Setup(x => x.CreateInitMessage())
                           .Returns(new InitMessage(new InitPayload(new FeatureSet())));
        var service = new PeerCommunicationService(NullLogger<PeerCommunicationService>.Instance,
                                                   _messageServiceMock.Object, _messageFactoryMock.Object,
                                                   _peerPubKey, _pingPongServiceMock.Object,
                                                   _serviceProviderMock.Object);
        Listen(service);

        // Act
        await service.InitializeAsync(TimeSpan.FromSeconds(30));

        // Assert
        _pingPongServiceMock.Verify(x => x.StartPingAsync(It.IsAny<CancellationToken>()), Times.Never);

        // Act
        RaiseMessage(new InitMessage(new InitPayload(new FeatureSet())));

        // Assert
        _pingPongServiceMock.Verify(x => x.StartPingAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Given_NoSubscriber_When_Constructed_Then_DoesNotListenToTheMessageService()
    {
        // Arrange - NL-239: listening before the peer service above subscribed lost the peer's init
        var subscriptions = 0;
        _messageServiceMock.SetupAdd(x => x.OnMessageReceived += It.IsAny<EventHandler<IMessage?>>())
                           .Callback(() => subscriptions++);

        // Act
        using var service = new PeerCommunicationService(NullLogger<PeerCommunicationService>.Instance,
                                                         _messageServiceMock.Object, _messageFactoryMock.Object,
                                                         _peerPubKey, _pingPongServiceMock.Object,
                                                         _serviceProviderMock.Object);
        var beforeSubscriber = subscriptions;
        service.MessageReceived += (_, _) => { };
        service.MessageReceived += (_, _) => { };

        // Assert
        Assert.Equal(0, beforeSubscriber);
        Assert.Equal(1, subscriptions);
    }

    [Fact]
    public void Given_Subscriber_When_PeerSendsInit_Then_SubscriberGetsIt()
    {
        // Arrange
        using var service = new PeerCommunicationService(NullLogger<PeerCommunicationService>.Instance,
                                                         _messageServiceMock.Object, _messageFactoryMock.Object,
                                                         _peerPubKey, _pingPongServiceMock.Object,
                                                         _serviceProviderMock.Object);
        IMessage? received = null;
        service.MessageReceived += (_, m) => received = m;
        var init = new InitMessage(new InitPayload(new FeatureSet()));

        // Act
        RaiseMessage(init);

        // Assert
        Assert.Same(init, received);
    }

    [Fact]
    public void Given_ConcurrentDisconnects_When_Disconnecting_Then_DisconnectEventFiresOnce()
    {
        // Arrange
        var service = CreateInitializedService();
        var disconnectCount = 0;
        service.DisconnectEvent += (_, _) => Interlocked.Increment(ref disconnectCount);

        // Act
        Parallel.For(0, 8, _ => service.Disconnect(new ConnectionException("test")));

        // Assert
        Assert.Equal(1, disconnectCount);
    }

    [Fact]
    public void Given_DisposedService_When_Disconnecting_Then_NothingThrowsAndNoEventFires()
    {
        // Arrange
        var service = CreateInitializedService();
        var disconnectRaised = false;
        service.DisconnectEvent += (_, _) => disconnectRaised = true;
        service.Dispose();

        // Act
        var exception = Record.Exception(() => service.Disconnect());

        // Assert
        Assert.Null(exception);
        Assert.False(disconnectRaised);
    }

    [Fact]
    public void Given_ChannelErrorWithChannelId_When_Disconnecting_Then_ErrorScopedToTheChannelIsSent()
    {
        // Arrange
        var service = CreateInitializedService();
        var sentMessages = CaptureSentMessages();
        var channelId = new ChannelId(Enumerable.Repeat((byte)0x07, 32).ToArray());

        // Act
        service.Disconnect(new ChannelErrorException("internal", channelId, "bad channel"));

        // Assert
        var error = Assert.IsType<ErrorMessage>(Assert.Single(sentMessages));
        Assert.Equal(channelId, error.Payload.ChannelId);
        Assert.Equal("bad channel", Encoding.UTF8.GetString(error.Payload.Data!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Given_ChannelErrorWithoutChannelId_When_Disconnecting_Then_WarningIsSentInsteadOfAllZeroError(
        bool zeroChannelId)
    {
        // Arrange (BOLT 1: an all-zero channel_id error makes the peer fail every channel with us)
        var service = CreateInitializedService();
        var sentMessages = CaptureSentMessages();
        var exception = zeroChannelId
                            ? new ChannelErrorException("internal", ChannelId.Zero, "bad channel")
                            : new ChannelErrorException("internal", "bad channel");

        // Act
        service.Disconnect(exception);

        // Assert
        Assert.DoesNotContain(sentMessages, m => m is ErrorMessage);
        var warning = Assert.IsType<WarningMessage>(Assert.Single(sentMessages));
        Assert.Equal(ChannelId.Zero, warning.Payload.ChannelId);
        Assert.Equal("bad channel", Encoding.UTF8.GetString(warning.Payload.Data!));
    }

    [Fact]
    public async Task Given_MessageServiceRaisesConnectionException_When_Raised_Then_ConnectionIsClosed()
    {
        // Arrange (a malformed message: MessageService already sent the warning, we must close the connection)
        var service = CreateInitializedService();
        var sentMessages = CaptureSentMessages();
        var disposedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _messageServiceMock.Setup(x => x.Dispose()).Callback(() => disposedTcs.TrySetResult());
        Exception? disconnectException = null;
        var disconnected = false;
        service.DisconnectEvent += (_, e) =>
        {
            disconnected = true;
            disconnectException = e;
        };
        var connectionException = new ConnectionException("Error received from transportService");

        // Act
        _messageServiceMock.Raise(x => x.OnExceptionRaised += null, _messageServiceMock.Object, connectionException);

        // Assert
        Assert.True(disconnected);
        Assert.Same(connectionException, disconnectException);
        Assert.Empty(sentMessages);
        await disposedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        _messageServiceMock.Verify(x => x.Dispose(), Times.Once);
    }

    [Fact]
    public async Task Given_MessageServiceDisposeBlocks_When_Disconnecting_Then_DisconnectDoesNotWaitForIt()
    {
        // Arrange (Disconnect often runs on the transport read loop, e.g. an init rejection, and disposing the
        // transport waits up to 5 s for that same loop to end, so it must not dispose inline)
        var service = CreateInitializedService();
        _ = CaptureSentMessages();
        using var releaseDispose = new ManualResetEventSlim(false);
        var disposeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _messageServiceMock.Setup(x => x.Dispose()).Callback(() =>
        {
            disposeStarted.TrySetResult();
            releaseDispose.Wait(TimeSpan.FromSeconds(10));
        });
        var disconnected = false;
        service.DisconnectEvent += (_, _) => disconnected = true;

        try
        {
            // Act
            var disconnectTask = Task.Run(() => service.Disconnect(new WarningException("Incompatible features")),
                                          TestContext.Current.CancellationToken);
            var finished = await Task.WhenAny(disconnectTask, Task.Delay(TimeSpan.FromSeconds(3),
                                                                          TestContext.Current.CancellationToken));

            // Assert
            Assert.Same(disconnectTask, finished);
            Assert.True(disconnected);
            await disposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            releaseDispose.Set();
        }
    }

    private List<IMessage> CaptureSentMessages()
    {
        var sentMessages = new List<IMessage>();
        _messageServiceMock
           .Setup(x => x.SendMessageAsync(It.IsAny<IMessage>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
           .Callback((IMessage message, bool _, CancellationToken _) => sentMessages.Add(message))
           .Returns(Task.CompletedTask);
        return sentMessages;
    }

    private void SetupUnitOfWork()
    {
        var unitOfWorkMock = new Mock<IUnitOfWork> { DefaultValue = DefaultValue.Mock };
        var scopedProviderMock = new Mock<IServiceProvider>();
        scopedProviderMock.Setup(x => x.GetService(typeof(IUnitOfWork))).Returns(unitOfWorkMock.Object);
        var scopeMock = new Mock<IServiceScope>();
        scopeMock.SetupGet(x => x.ServiceProvider).Returns(scopedProviderMock.Object);
        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        scopeFactoryMock.Setup(x => x.CreateScope()).Returns(scopeMock.Object);
        _serviceProviderMock.Setup(x => x.GetService(typeof(IServiceScopeFactory))).Returns(scopeFactoryMock.Object);
    }

    [Fact]
    public void Given_NoMessageYet_When_ReadingLastMessageReceivedAt_Then_NullAndSetOnTheFirstMessage()
    {
        // Arrange - NL-251: the ping before commitment_signed needs to know when the peer was last heard from
        using var service = new PeerCommunicationService(NullLogger<PeerCommunicationService>.Instance,
                                                         _messageServiceMock.Object, _messageFactoryMock.Object,
                                                         _peerPubKey, _pingPongServiceMock.Object,
                                                         _serviceProviderMock.Object);
        Listen(service);
        var before = service.LastMessageReceivedAt;
        var start = DateTimeOffset.UtcNow;

        // Act
        RaiseMessage(new InitMessage(new InitPayload(new FeatureSet())));

        // Assert
        Assert.Null(before);
        Assert.NotNull(service.LastMessageReceivedAt);
        Assert.InRange(service.LastMessageReceivedAt!.Value, start, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Given_PingLoopNotStarted_When_Pinging_Then_FalseWithoutAPing()
    {
        // Arrange - our init is not sent yet, so no ping may go out (BOLT 1)
        var service = CreateInitializedService();

        // Act
        var answered = await service.PingAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        // Assert
        Assert.False(answered);
        _pingPongServiceMock.Verify(x => x.PingAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
                                    Times.Never);
    }

    [Fact]
    public async Task Given_NewService_When_PingIsRaisedBeforeAndAfterInit_Then_OnlyTheOneAfterInitIsSent()
    {
        // Arrange - the ping handler is subscribed from the start, so a PingAsync right after the ping loop is marked
        // started never records a ping that nobody sends (it would time out and disconnect a healthy peer)
        _messageFactoryMock.Setup(x => x.CreateInitMessage())
                           .Returns(new InitMessage(new InitPayload(new FeatureSet())));
        var service = new PeerCommunicationService(NullLogger<PeerCommunicationService>.Instance,
                                                   _messageServiceMock.Object, _messageFactoryMock.Object,
                                                   _peerPubKey, _pingPongServiceMock.Object,
                                                   _serviceProviderMock.Object);
        Listen(service);
        _pingPongServiceMock.VerifyAdd(x => x.OnPingMessageReady += It.IsAny<EventHandler<IMessage>>(), Times.Once);

        // Act - before the peer's init no ping may go out (BOLT 1)
        _pingPongServiceMock.Raise(x => x.OnPingMessageReady += null, _pingPongServiceMock.Object,
                                   new PingMessage());

        // Assert
        _messageServiceMock.Verify(
            x => x.SendMessageAsync(It.IsAny<PingMessage>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Act - after both inits the ping is sent, and the loop start subscribes nothing a second time
        await service.InitializeAsync(TimeSpan.FromSeconds(30));
        RaiseMessage(new InitMessage(new InitPayload(new FeatureSet())));
        _pingPongServiceMock.Raise(x => x.OnPingMessageReady += null, _pingPongServiceMock.Object,
                                   new PingMessage());

        // Assert
        _messageServiceMock.Verify(
            x => x.SendMessageAsync(It.IsAny<PingMessage>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _pingPongServiceMock.VerifyAdd(x => x.OnPingMessageReady += It.IsAny<EventHandler<IMessage>>(), Times.Once);

        service.Disconnect();
    }

    [Fact]
    public async Task Given_RealPingPongService_When_PingingAfterInit_Then_ThePeersPongAnswersIt()
    {
        // Arrange
        _messageFactoryMock.Setup(x => x.CreatePingMessage()).Returns(() => new PingMessage());
        _messageFactoryMock.Setup(x => x.CreateInitMessage())
                           .Returns(new InitMessage(new InitPayload(new FeatureSet())));
        SetupUnitOfWork();
        var pingPongService = new PingPongService(_messageFactoryMock.Object,
                                                  Options.Create(new NodeOptions
                                                  {
                                                      NetworkTimeout = TimeSpan.FromSeconds(30)
                                                  }));
        var service = new PeerCommunicationService(NullLogger<PeerCommunicationService>.Instance,
                                                   _messageServiceMock.Object, _messageFactoryMock.Object,
                                                   _peerPubKey, pingPongService, _serviceProviderMock.Object);
        Listen(service);
        var pings = 0;
        _messageServiceMock
           .Setup(x => x.SendMessageAsync(It.IsAny<PingMessage>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
           .Callback((IMessage ping, bool _, CancellationToken _) =>
            {
                Interlocked.Increment(ref pings);
                _ = Task.Run(() => RaiseMessage(new PongMessage(((PingMessage)ping).Payload.NumPongBytes)));
            })
           .Returns(Task.CompletedTask);
        await service.InitializeAsync(TimeSpan.FromSeconds(30));
        RaiseMessage(new InitMessage(new InitPayload(new FeatureSet())));

        // Act
        var answered = await service.PingAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert - answered (by a new ping, or by the keep-alive one if it was still in flight)
        Assert.True(answered);
        Assert.InRange(Volatile.Read(ref pings), 1, 2);

        service.Disconnect();
    }

    [Fact]
    public async Task Given_SlowChannelConsumer_When_PingArrives_Then_ThePongIsAnsweredWhileTheHandlerIsStuck()
    {
        // Arrange - NL-108: a channel handler runs on the peer's inbound loop, behind the queues, so a ping is
        // deserialized and answered even while that loop is stuck on an earlier message. The stack here is the real
        // MessageService (its per-peer consumer) under a real PeerCommunicationService; the subscriber below mimics
        // the peer manager: it only queues, never handles inline.
        var transportServiceMock = new Mock<ITransportService>();
        transportServiceMock.SetupGet(t => t.IsConnected).Returns(true);
        var pongSent = new TaskCompletionSource<IMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        transportServiceMock
           .Setup(t => t.WriteMessageAsync(It.IsAny<IMessage>(), It.IsAny<CancellationToken>()))
           .Callback<IMessage, CancellationToken>((m, _) =>
            {
                if (m.Type == MessageTypes.Pong)
                    pongSent.TrySetResult(m);
            })
           .Returns(Task.CompletedTask);

        var initMessage = new InitMessage(new InitPayload(new FeatureSet()));
        _messageFactoryMock.Setup(x => x.CreateInitMessage(null)).Returns(initMessage);
        var channelMessageMock = new Mock<IMessage>();
        channelMessageMock.SetupGet(m => m.Type).Returns(MessageTypes.UpdateAddHtlc);
        var ping = CreatePing(0);
        var byType = new Dictionary<ushort, IMessage>
        {
            [(ushort)MessageTypes.Init] = initMessage,
            [(ushort)MessageTypes.UpdateAddHtlc] = channelMessageMock.Object,
            [(ushort)MessageTypes.Ping] = ping
        };
        var serializerMock = new Mock<IMessageSerializer>();
        serializerMock
           .Setup(m => m.DeserializeMessageAsync(It.IsAny<Stream>()))
           .ReturnsAsync((Stream stream) =>
            {
                var position = stream.Position;
                Span<byte> header = stackalloc byte[2];
                stream.ReadExactly(header);
                stream.Position = position;
                return byType[System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(header)];
            });

        using var messageService = new MessageService(NullLogger<MessageService>.Instance, serializerMock.Object,
                                                      transportServiceMock.Object);
        var service = new PeerCommunicationService(NullLogger<PeerCommunicationService>.Instance, messageService,
                                                   _messageFactoryMock.Object, _peerPubKey,
                                                   _pingPongServiceMock.Object, _serviceProviderMock.Object);

        // The "peer manager": channel messages are queued for a slow inbound loop, never handled inline
        var inbound = System.Threading.Channels.Channel.CreateUnbounded<IMessage>();
        var initHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerStuck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.MessageReceived += (_, m) =>
        {
            if (m is null)
                return;

            if (m.Type == MessageTypes.Init)
                initHandled.TrySetResult();
            else
                inbound.Writer.TryWrite(m);
        };
        var slowLoop = Task.Run(async () =>
        {
            await foreach (var _ in inbound.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
            {
                // The slow channel handler: stuck until the test releases it
                handlerStuck.TrySetResult();
                await releaseHandler.Task.WaitAsync(TestContext.Current.CancellationToken);
            }
        }, TestContext.Current.CancellationToken);

        // Act: init, then a channel message whose handler stalls, then a ping
        await service.InitializeAsync(TimeSpan.FromSeconds(30));
        RaiseRaw(transportServiceMock, messageService, (ushort)MessageTypes.Init);
        await initHandled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        RaiseRaw(transportServiceMock, messageService, (ushort)MessageTypes.UpdateAddHtlc);
        await handlerStuck.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        RaiseRaw(transportServiceMock, messageService, (ushort)MessageTypes.Ping);
        var pong = await pongSent.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert: the pong went out while the channel handler is still stuck on the earlier message
        Assert.False(releaseHandler.Task.IsCompleted);
        Assert.Equal(MessageTypes.Pong, pong.Type);

        releaseHandler.TrySetResult();
        inbound.Writer.TryComplete();
        await slowLoop.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        service.Dispose();
    }

    /// <summary>
    /// Feeds a raw frame (its two type bytes, enough for the serializer stub) into the message service the way the
    /// transport read loop does.
    /// </summary>
    private static void RaiseRaw(Mock<ITransportService> transportServiceMock, MessageService messageService,
                                 ushort type)
    {
        transportServiceMock.Raise(t => t.MessageReceived += null, messageService,
                                   new MemoryStream([(byte)(type >> 8), (byte)type]));
    }
}