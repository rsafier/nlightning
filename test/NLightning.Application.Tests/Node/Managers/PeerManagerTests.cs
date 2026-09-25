using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NLightning.Infrastructure.Protocol.Models;
using NLightning.Tests.Utils.Channels;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Node.Managers;

using Application.Node.Managers;
using Domain.Channels.Enums;
using Domain.Channels.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Events;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.Options;
using Domain.Node.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Infrastructure.Node.ValueObjects;
using Infrastructure.Transport.Events;
using Infrastructure.Transport.Interfaces;

// ReSharper disable AccessToDisposedClosure
public class PeerManagerTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    private readonly CompactPubKey _compactPubKey =
        new PubKey("028d7500dd4c12685d1f568b4c2b5048e8534b873319f3a8daa612b469132ec7f7").ToBytes();

    private readonly Mock<IChannelManager> _mockChannelManager = new();
    private readonly Mock<ILogger<PeerManager>> _mockLogger = new();
    private readonly Mock<IPeerServiceFactory> _mockPeerServiceFactory = new();
    private readonly Mock<IPeerService> _mockPeerService = new();
    private readonly Mock<ITcpService> _mockTcpService = new();
    private readonly Mock<IChannelMessage> _mockChannelMessage = new();
    private readonly Mock<IChannelMessage> _mockResponseMessage = new();
    private readonly FakeServiceProvider _fakeServiceProvider = new();
    private readonly Mock<IUnitOfWork> _mockUnitOfWork = new();
    private readonly Mock<IPeerDbRepository> _mockPeerDbRepository = new();

    private const string ExpectedHost = "127.0.0.1";
    private const int ExpectedPort = 9735;
    private const string ExpectedType = "IPv4";

    public PeerManagerTests()
    {
        // Set up the mock peer service
        _mockPeerService.SetupGet(p => p.PeerPubKey).Returns(_compactPubKey);
        _mockPeerService.SetupGet(p => p.Features).Returns(new FeatureOptions());
        _mockPeerService.Setup(p => p.SendMessageAsync(It.IsAny<IChannelMessage>())).Returns(Task.CompletedTask);
        _mockPeerService.Setup(p => p.SendWarningAsync(It.IsAny<WarningException>())).Returns(Task.CompletedTask);

        // Set up the mock channel message
        _mockChannelMessage.SetupGet(m => m.Type).Returns(MessageTypes.OpenChannel);

        // Set up the peer service factory to return our mock peer service
        _mockPeerServiceFactory
           .Setup(f => f.CreateConnectedPeerAsync(It.IsAny<CompactPubKey>(), It.IsAny<TcpClient>()))
           .ReturnsAsync(_mockPeerService.Object);

        _mockPeerServiceFactory
           .Setup(f => f.CreateConnectingPeerAsync(It.IsAny<TcpClient>()))
           .ReturnsAsync(_mockPeerService.Object);

        // The channel manager raises its replies through OnResponseMessageReady (under the channel lock)
        SetupChannelManagerReplies(_mockResponseMessage.Object);

        // Set up unit of work and repositories
        _mockUnitOfWork.Setup(u => u.PeerDbRepository).Returns(_mockPeerDbRepository.Object);
        _mockUnitOfWork.Setup(u => u.GetPeersForStartupAsync()).ReturnsAsync(() => []);
        _mockPeerDbRepository.Setup(r => r.AddOrUpdateAsync(It.IsAny<PeerModel>())).Returns(Task.CompletedTask);
        _fakeServiceProvider.AddService(typeof(IUnitOfWork), _mockUnitOfWork.Object);
    }

    [Fact]
    public async Task Given_ValidPeerAddress_When_ConnectToPeerAsync_IsCalled_Then_PeerIsAdded()
    {
        // Given
        var peerManager = CreatePeerManager();

        var peerAddressInfo = new PeerAddressInfo($"{_compactPubKey}@127.0.0.1:9735");
        var peerAddress = new PeerAddress(peerAddressInfo);

        // Mock the TCP service to return a connected peer
        var mockTcpClient = new Mock<TcpClient>();
        var mockConnectedPeer = new ConnectedPeer(_compactPubKey, ExpectedHost, ExpectedPort, mockTcpClient.Object);
        _mockTcpService.Setup(t => t.ConnectToPeerAsync(peerAddress))
                       .ReturnsAsync(mockConnectedPeer);

        // When
        await peerManager.ConnectToPeerAsync(peerAddressInfo);

        // Then
        Assert.NotNull(peerManager.GetPeer(_compactPubKey));

        // Verify the TCP service was called
        _mockTcpService.Verify(t => t.ConnectToPeerAsync(peerAddress), Times.Once);

        // Verify peer service factory was called
        _mockPeerServiceFactory.Verify(f => f.CreateConnectedPeerAsync(_compactPubKey, mockTcpClient.Object),
                                       Times.Once);

        // Verify event handlers were set up
        _mockPeerService.VerifyAdd(p => p.OnDisconnect += It.IsAny<EventHandler<PeerDisconnectedEventArgs>>(),
                                   Times.Once);
        _mockPeerService.VerifyAdd(p => p.OnChannelMessageReceived += It.IsAny<EventHandler<ChannelMessageEventArgs>>(),
                                   Times.Once);

        // Verify repository methods were called
        _mockPeerDbRepository.Verify(r => r.AddOrUpdateAsync(It.IsAny<PeerModel>()), Times.Once);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task Given_ConnectedPeer_When_ConnectToPeerAsyncAgain_Then_InvalidOperationException()
    {
        // Arrange
        var peerManager = await CreatePeerManagerWithPeerAsync();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => peerManager.ConnectToPeerAsync(new PeerAddressInfo($"{_compactPubKey}@127.0.0.1:9735")));
        Assert.Single(peerManager.ListPeers());
    }

    [Fact]
    public async Task Given_ConnectionError_When_ConnectToPeerAsync_IsCalled_Then_ExceptionIsThrown()
    {
        // Given
        var peerManager = CreatePeerManager();

        var peerAddressInfo = new PeerAddressInfo($"{_compactPubKey}@127.0.0.1:9735");
        var peerAddress = new PeerAddress(peerAddressInfo);
        var expectedError =
            new ConnectionException("Failed to connect to peer 127.0.0.1:9735");

        // Mock TCP service to throw a connection exception
        _mockTcpService.Setup(t => t.ConnectToPeerAsync(peerAddress))
                       .ThrowsAsync(expectedError);

        // When & Then
        var exception =
            await Assert.ThrowsAsync<ConnectionException>(() => peerManager.ConnectToPeerAsync(peerAddressInfo));
        Assert.Equal(expectedError.Message, exception.Message);

        // Verify no peer was added
        Assert.Empty(peerManager.ListPeers());
    }

    [Fact]
    public async Task Given_UnreachablePeerWithChannels_When_StartAsync_Then_ChannelsAreStillRegistered()
    {
        // Arrange
        var peerManager = CreatePeerManager();
        var openChannel = CreateChannel(ChannelState.Open, 1);
        var closedChannel = CreateChannel(ChannelState.Closed, 2);
        var staleChannel = CreateChannel(ChannelState.Stale, 3);
        var peer = new PeerModel(_compactPubKey, ExpectedHost, ExpectedPort, ExpectedType)
        {
            Channels = [openChannel, closedChannel, staleChannel]
        };
        _mockUnitOfWork.Setup(u => u.GetPeersForStartupAsync()).ReturnsAsync([peer]);
        _mockTcpService.Setup(t => t.ConnectToPeerAsync(It.IsAny<PeerAddress>()))
                       .ThrowsAsync(new ConnectionException("Failed to connect to peer"));

        // Act
        await peerManager.StartAsync(TestContext.Current.CancellationToken);

        // Assert
        _mockChannelManager.Verify(cm => cm.RegisterExistingChannelAsync(openChannel), Times.Once);
        _mockChannelManager.Verify(cm => cm.RegisterExistingChannelAsync(closedChannel), Times.Never);
        _mockChannelManager.Verify(cm => cm.RegisterExistingChannelAsync(staleChannel), Times.Never);
        Assert.Empty(peerManager.ListPeers());
        _mockTcpService.Verify(t => t.StartListeningAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_Start_Then_RegisteredBeforeConnect()
    {
        // Arrange: two peers, each registration completes asynchronously
        var peerManager = CreatePeerManager();
        var channelA = CreateChannel(ChannelState.Open, 1);
        var channelB = CreateChannel(ChannelState.Open, 2);
        var otherPubKey = new PubKey("023da092f6980e58d2c037173180e9a465476026ee50f96695963e8efe436f54eb").ToBytes();
        var peerA = new PeerModel(_compactPubKey, ExpectedHost, ExpectedPort, ExpectedType) { Channels = [channelA] };
        var peerB = new PeerModel(otherPubKey, ExpectedHost, ExpectedPort, ExpectedType) { Channels = [channelB] };
        _mockUnitOfWork.Setup(u => u.GetPeersForStartupAsync()).ReturnsAsync([peerA, peerB]);

        var registered = 0;
        _mockChannelManager.Setup(cm => cm.RegisterExistingChannelAsync(It.IsAny<ChannelModel>()))
                           .Returns(async () =>
                            {
                                await Task.Delay(20);
                                Interlocked.Increment(ref registered);
                            });

        var registeredAtFirstConnect = -1;
        _mockTcpService.Setup(t => t.ConnectToPeerAsync(It.IsAny<PeerAddress>()))
                       .Callback(() => Interlocked.CompareExchange(ref registeredAtFirstConnect,
                                                                   Volatile.Read(ref registered), -1))
                       .ThrowsAsync(new ConnectionException("Failed to connect to peer"));

        // Act
        await peerManager.StartAsync(TestContext.Current.CancellationToken);

        // Assert: both channels were fully registered before the first connection attempt
        Assert.Equal(2, registeredAtFirstConnect);
        await peerManager.StopAsync();
    }

    [Fact]
    public async Task Given_RegistrationFails_When_Start_Then_OtherChannelsRegisteredAndPeerConnected()
    {
        // Arrange
        var peerManager = CreatePeerManager();
        var failingChannel = CreateChannel(ChannelState.Open, 1);
        var channel = CreateChannel(ChannelState.ReadyForUs, 2);
        var peer = new PeerModel(_compactPubKey, ExpectedHost, ExpectedPort, ExpectedType)
        {
            Channels = [failingChannel, channel]
        };
        _mockUnitOfWork.Setup(u => u.GetPeersForStartupAsync()).ReturnsAsync([peer]);
        _mockChannelManager.Setup(cm => cm.RegisterExistingChannelAsync(failingChannel))
                           .ThrowsAsync(new InvalidOperationException("signer failure"));
        _mockTcpService.Setup(t => t.ConnectToPeerAsync(It.IsAny<PeerAddress>()))
                       .ReturnsAsync(new ConnectedPeer(_compactPubKey, ExpectedHost, ExpectedPort,
                                                       new Mock<TcpClient>().Object));

        // Act
        await peerManager.StartAsync(TestContext.Current.CancellationToken);

        // Assert
        _mockChannelManager.Verify(cm => cm.RegisterExistingChannelAsync(channel), Times.Once);
        Assert.NotNull(peerManager.GetPeer(_compactPubKey));
    }

    [Fact]
    public async Task Given_PeerUnreachable_When_Start_Then_RetriesWithBackoffUntilConnected()
    {
        // Arrange
        var peerManager = CreatePeerManager();
        peerManager.ReconnectInitialDelay = TimeSpan.FromMilliseconds(10);
        peerManager.ReconnectMaxDelay = TimeSpan.FromMilliseconds(40);
        var peer = new PeerModel(_compactPubKey, ExpectedHost, ExpectedPort, ExpectedType)
        {
            Channels = [CreateChannel(ChannelState.Open, 1)]
        };
        _mockUnitOfWork.Setup(u => u.GetPeersForStartupAsync()).ReturnsAsync([peer]);

        var attempts = 0;
        _mockTcpService.Setup(t => t.ConnectToPeerAsync(It.IsAny<PeerAddress>()))
                       .Returns(() => Interlocked.Increment(ref attempts) <= 3
                                          ? Task.FromException<ConnectedPeer>(
                                              new ConnectionException("Failed to connect to peer"))
                                          : Task.FromResult(new ConnectedPeer(_compactPubKey, ExpectedHost,
                                                                ExpectedPort, new Mock<TcpClient>().Object)));

        // Act
        await peerManager.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => peerManager.GetPeer(_compactPubKey) is not null);

        // Assert: the startup attempt, two failed retries, then the successful one; no more after that
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(4, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task Given_PeerWithoutActiveChannelsUnreachable_When_Start_Then_NoRetry()
    {
        // Arrange
        var peerManager = CreatePeerManager();
        peerManager.ReconnectInitialDelay = TimeSpan.FromMilliseconds(10);
        var peer = new PeerModel(_compactPubKey, ExpectedHost, ExpectedPort, ExpectedType)
        {
            Channels = [CreateChannel(ChannelState.Closed, 1)]
        };
        _mockUnitOfWork.Setup(u => u.GetPeersForStartupAsync()).ReturnsAsync([peer]);
        _mockTcpService.Setup(t => t.ConnectToPeerAsync(It.IsAny<PeerAddress>()))
                       .ThrowsAsync(new ConnectionException("Failed to connect to peer"));

        // Act
        await peerManager.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        _mockTcpService.Verify(t => t.ConnectToPeerAsync(It.IsAny<PeerAddress>()), Times.Once);
    }

    [Fact]
    public async Task Given_Retrying_When_StopAsync_Then_RetriesStop()
    {
        // Arrange
        var peerManager = CreatePeerManager();
        peerManager.ReconnectInitialDelay = TimeSpan.FromMilliseconds(10);
        peerManager.ReconnectMaxDelay = TimeSpan.FromMilliseconds(10);
        var peer = new PeerModel(_compactPubKey, ExpectedHost, ExpectedPort, ExpectedType)
        {
            Channels = [CreateChannel(ChannelState.Open, 1)]
        };
        _mockUnitOfWork.Setup(u => u.GetPeersForStartupAsync()).ReturnsAsync([peer]);
        var attempts = 0;
        _mockTcpService.Setup(t => t.ConnectToPeerAsync(It.IsAny<PeerAddress>()))
                       .Callback(() => Interlocked.Increment(ref attempts))
                       .ThrowsAsync(new ConnectionException("Failed to connect to peer"));
        await peerManager.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => Volatile.Read(ref attempts) >= 3);

        // Act
        await peerManager.StopAsync();
        var attemptsAtStop = Volatile.Read(ref attempts);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(attemptsAtStop, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task Given_StartAsync_When_Called_Then_TcpServiceStartsListening()
    {
        // Given
        var peerManager = CreatePeerManager();
        var cancellationToken = CancellationToken.None;

        // Setup for loading startup peers
        _mockUnitOfWork.Setup(u => u.GetPeersForStartupAsync()).ReturnsAsync(new List<PeerModel>());

        // When
        await peerManager.StartAsync(cancellationToken);

        // Then
        _mockTcpService.Verify(t => t.StartListeningAsync(It.IsAny<CancellationToken>()), Times.Once);
        _mockTcpService.VerifyAdd(t => t.OnNewPeerConnected += It.IsAny<EventHandler<NewPeerConnectedEventArgs>>(),
                                  Times.Once);
        _mockUnitOfWork.Verify(u => u.GetPeersForStartupAsync(), Times.Once);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task Given_NewPeerConnected_When_EventRaised_Then_PeerIsAddedThroughFactory()
    {
        // Given
        var peerManager = CreatePeerManager();

        var mockTcpClient = new Mock<TcpClient>();
        var eventArgs = new NewPeerConnectedEventArgs(ExpectedHost, ExpectedPort, mockTcpClient.Object);

        // When
        await peerManager.StartAsync(CancellationToken.None);

        // Simulate the TCP service raising the event
#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
        // ReSharper disable once MethodHasAsyncOverload
        _mockTcpService.Raise(t => t.OnNewPeerConnected += null, null, eventArgs);
#pragma warning restore CS8625 // Cannot convert null literal to non-nullable reference type.

        // Then
        _mockPeerServiceFactory.Verify(f => f.CreateConnectingPeerAsync(mockTcpClient.Object), Times.Once);

        var peers = peerManager.ListPeers();
        Assert.Single(peers);
        Assert.Equal(_compactPubKey, peers[0].NodeId);
    }

    [Fact]
    public async Task Given_ExistingPeer_When_DisconnectPeer_IsCalled_Then_PeerIsDisconnected()
    {
        // Given
        var peerManager = await CreatePeerManagerWithPeerAsync();

        // When
        peerManager.DisconnectPeer(_compactPubKey);

        // Then
        _mockPeerService.Verify(p => p.Disconnect(null), Times.Once);
    }

    [Fact]
    public void Given_NonExistingPeer_When_DisconnectPeer_IsCalled_Then_LogWarning()
    {
        // Given
        var peerManager = CreatePeerManager();

        // When
        peerManager.DisconnectPeer(_compactPubKey);

        // Then
        _mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("Peer") && o.ToString()!.Contains("not found")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task Given_PeerChannelMessage_When_EventRaised_Then_ChannelManagerIsInvoked()
    {
        // Given
        await CreatePeerManagerWithPeerAsync();
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockChannelManager
           .Setup(cm => cm.HandleChannelMessageAsync(It.IsAny<IChannelMessage>(), It.IsAny<FeatureOptions>(),
                                                     It.IsAny<CompactPubKey>()))
           .Callback(handled.SetResult)
           .ReturnsAsync([]);

        // When
        RaiseChannelMessage(_mockChannelMessage.Object);
        await handled.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Then
        _mockChannelManager.Verify(cm => cm.HandleChannelMessageAsync(
                                       _mockChannelMessage.Object,
                                       It.IsAny<FeatureOptions>(),
                                       _compactPubKey), Times.Once);
    }

    [Fact]
    public async Task Given_ChannelMessageWithResponse_When_Processed_Then_ResponseIsSentToPeer()
    {
        // Given
        await CreatePeerManagerWithPeerAsync();
        var sent = CaptureSentMessages(1);

        // When
        RaiseChannelMessage(_mockChannelMessage.Object);

        // Then
        Assert.Equal(new[] { _mockResponseMessage.Object }, await sent.WaitAsync(s_timeout,
                                                                         TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_HandlerReturnsThree_When_Processed_Then_SentInOrder()
    {
        // Arrange (NL-193: several replies to one message go out in order, one send at a time)
        await CreatePeerManagerWithPeerAsync();
        var replies = CreateMessages(3);
        SetupChannelManagerReplies(replies);
        var sent = CaptureSentMessages(3, slowFirstSend: true);

        // Act
        RaiseChannelMessage(_mockChannelMessage.Object);

        // Assert
        Assert.Equal(replies, await sent.WaitAsync(s_timeout, TestContext.Current.CancellationToken));
        Assert.Equal(1, _maxConcurrentSends);
    }

    [Fact]
    public async Task Given_EventAndReplyInterleave_When_Processed_Then_FifoPreserved()
    {
        // Arrange (NL-193: replies and out-of-band messages, e.g. channel_ready on a funding confirmation, share one
        // FIFO: whatever was enqueued first is sent first, never concurrently)
        await CreatePeerManagerWithPeerAsync();
        var messages = CreateMessages(4);
        var firstMessage = CreateInboundMessage();
        var secondMessage = CreateInboundMessage();
        _mockChannelManager
           .Setup(cm => cm.HandleChannelMessageAsync(firstMessage, It.IsAny<FeatureOptions>(),
                                                     It.IsAny<CompactPubKey>()))
           .Callback(() =>
            {
                RaiseResponse(messages[0]);
                RaiseResponse(messages[1]); // an event raised while the first transition holds the lock
            })
           .ReturnsAsync([]);
        _mockChannelManager
           .Setup(cm => cm.HandleChannelMessageAsync(secondMessage, It.IsAny<FeatureOptions>(),
                                                     It.IsAny<CompactPubKey>()))
           .Callback(() => RaiseResponse(messages[3]))
           .ReturnsAsync([]);
        var sent = CaptureSentMessages(4, slowFirstSend: true);

        // Act
        RaiseChannelMessage(firstMessage);
        await WaitUntilAsync(() => _sendsStarted >= 1);
        RaiseResponse(messages[2]); // a block event on another thread, while the first send is still in flight
        RaiseChannelMessage(secondMessage);

        // Assert
        Assert.Equal(messages, await sent.WaitAsync(s_timeout, TestContext.Current.CancellationToken));
        Assert.Equal(1, _maxConcurrentSends);
    }

    [Fact]
    public async Task Given_TwoMessagesFromPeer_When_FirstIsStillHandled_Then_SecondWaitsAndReadLoopIsNotBlocked()
    {
        // Arrange (NL-033: one ordered inbound loop per peer; NL-108 partial: the transport read loop only queues)
        await CreatePeerManagerWithPeerAsync();
        var firstMessage = CreateInboundMessage();
        var secondMessage = CreateInboundMessage();
        var gate = new TaskCompletionSource<IReadOnlyList<IChannelMessage>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<IChannelMessage>();
        _mockChannelManager
           .Setup(cm => cm.HandleChannelMessageAsync(firstMessage, It.IsAny<FeatureOptions>(),
                                                     It.IsAny<CompactPubKey>()))
           .Returns(() =>
            {
                lock (order)
                    order.Add(firstMessage);
                firstEntered.SetResult();
                return gate.Task;
            });
        _mockChannelManager
           .Setup(cm => cm.HandleChannelMessageAsync(secondMessage, It.IsAny<FeatureOptions>(),
                                                     It.IsAny<CompactPubKey>()))
           .Returns(() =>
            {
                lock (order)
                    order.Add(secondMessage);
                secondHandled.SetResult();
                return Task.FromResult<IReadOnlyList<IChannelMessage>>([]);
            });

        // Act
        RaiseChannelMessage(firstMessage);
        await firstEntered.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        RaiseChannelMessage(secondMessage); // returns although the first message is still being handled
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var secondStartedEarly = secondHandled.Task.IsCompleted;
        gate.SetResult([]);
        await secondHandled.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(secondStartedEarly);
        Assert.Equal(new[] { firstMessage, secondMessage }, order);
    }

    [Fact]
    public async Task Given_ReplyQueued_When_NextMessageFailsWithError_Then_ErrorIsSentAfterTheReply()
    {
        // Arrange (the `error` goes through the outbox too, so it can't overtake a reply queued before it)
        await CreatePeerManagerWithPeerAsync();
        var channelId = new ChannelId(Enumerable.Repeat((byte)0x41, 32).ToArray());
        var reply = CreateMessages(1)[0];
        var firstMessage = CreateInboundMessage(channelId);
        var failingMessage = CreateInboundMessage(channelId);
        var afterError = CreateInboundMessage(channelId);
        _mockChannelManager
           .Setup(cm => cm.HandleChannelMessageAsync(firstMessage, It.IsAny<FeatureOptions>(),
                                                     It.IsAny<CompactPubKey>()))
           .Callback(() => RaiseResponse(reply))
           .ReturnsAsync([reply]);
        _mockChannelManager
           .Setup(cm => cm.HandleChannelMessageAsync(failingMessage, It.IsAny<FeatureOptions>(),
                                                     It.IsAny<CompactPubKey>()))
           .ThrowsAsync(new ChannelErrorException("bad", channelId, "bad message"));
        var events = new List<string>();
        _mockPeerService.Setup(p => p.SendMessageAsync(It.IsAny<IChannelMessage>()))
                        .Returns(async () =>
                         {
                             await Task.Delay(100);
                             lock (events)
                                 events.Add("reply");
                         });
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockPeerService.Setup(p => p.Disconnect(It.IsAny<Exception?>()))
                        .Callback(() =>
                         {
                             lock (events)
                                 events.Add("disconnect");
                             disconnected.TrySetResult();
                         });

        // Act
        RaiseChannelMessage(firstMessage);
        RaiseChannelMessage(failingMessage);
        RaiseChannelMessage(afterError);
        await disconnected.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(new[] { "reply", "disconnect" }, events);
        _mockChannelManager.Verify(cm => cm.HandleChannelMessageAsync(afterError, It.IsAny<FeatureOptions>(),
                                                                      It.IsAny<CompactPubKey>()), Times.Never);
    }

    [Fact]
    public async Task Given_ChannelErrorException_When_ProcessingChannelMessage_Then_PeerIsDisconnected()
    {
        // Given
        await CreatePeerManagerWithPeerAsync();
        var channelError = new ChannelErrorException("Test channel error", "Peer error message");
        _mockChannelManager
           .Setup(cm => cm.HandleChannelMessageAsync(It.IsAny<IChannelMessage>(),
                                                     It.IsAny<FeatureOptions>(),
                                                     It.IsAny<CompactPubKey>()))
           .ThrowsAsync(channelError);
        var disconnectTcs = CaptureDisconnect();

        // When
        RaiseChannelMessage(_mockChannelMessage.Object);
        var exception = await disconnectTcs.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Then
        Assert.Same(channelError, exception);
        _mockLogger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("Error handling channel message")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task
        Given_ChannelWarningException_When_ProcessingChannelMessage_Then_WarningIsLoggedButPeerStaysConnected()
    {
        // Given
        await CreatePeerManagerWithPeerAsync();
        var channelWarning = new ChannelWarningException("Test channel warning", "Peer warning message");
        _mockChannelManager
           .Setup(cm => cm.HandleChannelMessageAsync(It.IsAny<IChannelMessage>(),
                                                     It.IsAny<FeatureOptions>(),
                                                     It.IsAny<CompactPubKey>()))
           .ThrowsAsync(channelWarning);
        var warningTcs = CaptureWarning();

        // When
        RaiseChannelMessage(_mockChannelMessage.Object);
        await warningTcs.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Then
        _mockPeerService.Verify(p => p.Disconnect(It.IsAny<Exception?>()), Times.Never);
        _mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("Error handling channel message")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task Given_ChannelErrorWithoutChannelId_When_ProcessingChannelMessage_Then_ErrorIsScopedToTheChannel()
    {
        // Arrange (BOLT 1: an all-zero channel_id error would make the peer fail every channel with us)
        await CreatePeerManagerWithPeerAsync();
        var channelId = new ChannelId(Enumerable.Repeat((byte)0x42, 32).ToArray());
        SetupChannelMessage(MessageTypes.OpenChannel, channelId);
        var channelError = new ChannelErrorException("ChannelTypeTlv is not present", "Peer error message");
        _mockChannelManager
           .Setup(cm => cm.HandleChannelMessageAsync(It.IsAny<IChannelMessage>(), It.IsAny<FeatureOptions>(),
                                                     It.IsAny<CompactPubKey>()))
           .ThrowsAsync(channelError);
        var disconnectTcs = CaptureDisconnect();

        // Act
        RaiseChannelMessage(_mockChannelMessage.Object);
        var exception = await disconnectTcs.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Assert
        var sentError = Assert.IsType<ChannelErrorException>(exception);
        Assert.Equal(channelId, sentError.ChannelId);
        Assert.Equal("Peer error message", sentError.PeerMessage);
    }

    [Fact]
    public async Task Given_NotImplementedChannelMessage_When_Processing_Then_ChannelScopedWarningAndStaysConnected()
    {
        // Arrange (interim behavior for e.g. channel_reestablish until BOLT2 plan N7)
        await CreatePeerManagerWithPeerAsync();
        var channelId = new ChannelId(Enumerable.Repeat((byte)0x43, 32).ToArray());
        SetupChannelMessage(MessageTypes.ChannelReestablish, channelId);
        _mockChannelManager
           .Setup(cm => cm.HandleChannelMessageAsync(It.IsAny<IChannelMessage>(), It.IsAny<FeatureOptions>(),
                                                     It.IsAny<CompactPubKey>()))
           .ThrowsAsync(new ChannelWarningException("Ignoring ChannelReestablish", channelId, "not supported yet"));
        var warningTcs = CaptureWarning();

        // Act
        RaiseChannelMessage(_mockChannelMessage.Object);
        var warning = await warningTcs.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(channelId, Assert.IsType<ChannelWarningException>(warning).ChannelId);
        _mockPeerService.Verify(p => p.Disconnect(It.IsAny<Exception?>()), Times.Never);
    }

    [Fact]
    public async Task Given_ChannelWarningThatClosesConnection_When_Processing_Then_WarningIsSentByDisconnect()
    {
        // Arrange (BOLT 2 "send a `warning` and close the connection", e.g. update_fail_malformed_htlc without
        // BADONION: never an `error`, since we can't fail the channel on our side yet)
        await CreatePeerManagerWithPeerAsync();
        var channelId = new ChannelId(Enumerable.Repeat((byte)0x45, 32).ToArray());
        SetupChannelMessage(MessageTypes.UpdateFailMalformedHtlc, channelId);
        _mockChannelManager
           .Setup(cm => cm.HandleChannelMessageAsync(It.IsAny<IChannelMessage>(), It.IsAny<FeatureOptions>(),
                                                     It.IsAny<CompactPubKey>()))
           .ThrowsAsync(new ChannelWarningException("no BADONION", "BADONION must be set")
           {
               CloseConnection = true
           });
        var disconnectTcs = CaptureDisconnect();

        // Act
        RaiseChannelMessage(_mockChannelMessage.Object);
        var exception = await disconnectTcs.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Assert
        var warning = Assert.IsType<ChannelWarningException>(exception);
        Assert.Equal(channelId, warning.ChannelId);
        Assert.True(warning.CloseConnection);
        Assert.Equal("BADONION must be set", warning.PeerMessage);
        _mockPeerService.Verify(p => p.SendWarningAsync(It.IsAny<WarningException>()), Times.Never);
    }

    [Fact]
    public async Task Given_UnexpectedException_When_ProcessingChannelMessage_Then_ChannelScopedWarningAndDisconnect()
    {
        // Arrange (our own bug must not fail the channel: warn for the channel, never an error, then disconnect)
        await CreatePeerManagerWithPeerAsync();
        var channelId = new ChannelId(Enumerable.Repeat((byte)0x44, 32).ToArray());
        SetupChannelMessage(MessageTypes.FundingCreated, channelId);
        _mockChannelManager
           .Setup(cm => cm.HandleChannelMessageAsync(It.IsAny<IChannelMessage>(), It.IsAny<FeatureOptions>(),
                                                     It.IsAny<CompactPubKey>()))
           .ThrowsAsync(new InvalidOperationException("database is down"));
        var disconnectTcs = CaptureDisconnect();

        // Act
        RaiseChannelMessage(_mockChannelMessage.Object);
        var exception = await disconnectTcs.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Assert
        var warning = Assert.IsType<ChannelWarningException>(exception);
        Assert.Equal(channelId, warning.ChannelId);
        Assert.IsType<InvalidOperationException>(warning.InnerException);
    }

    [Fact]
    public async Task Given_ResponseForUnknownPeer_When_Raised_Then_DroppedWithoutThrowing()
    {
        // Arrange (raised under a channel lock: it must never throw into the channel manager)
        CreatePeerManager();

        // Act
        var exception = Record.Exception(() => RaiseResponse(_mockResponseMessage.Object));

        // Assert
        Assert.Null(exception);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        _mockPeerService.Verify(p => p.SendMessageAsync(It.IsAny<IChannelMessage>()), Times.Never);
    }

    [Fact]
    public async Task Given_PeerDisconnection_When_EventRaised_Then_PeerIsRemovedFromManager()
    {
        // Given
        var peerManager = await CreatePeerManagerWithPeerAsync();

        // When
        _mockPeerService.Raise(p => p.OnDisconnect += null, _mockPeerService.Object,
                               new PeerDisconnectedEventArgs(_compactPubKey));

        // Then
        Assert.Null(peerManager.GetPeer(_compactPubKey));
        _mockPeerService.Verify(p => p.Dispose(), Times.Once);
        _mockLogger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("Peer") && o.ToString()!.Contains("disconnected")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task Given_StopAsync_When_Called_Then_AllPeersAreDisconnectedAndServiceIsStopped()
    {
        // Given
        var peerManager = await CreatePeerManagerWithPeerAsync();
        await peerManager.StartAsync(CancellationToken.None);
        var taskCompletionSource = new TaskCompletionSource();
        _mockPeerService.Setup(x => x.Disconnect(null)).Callback(taskCompletionSource.SetResult);

        // When
        _ = peerManager.StopAsync();
        await taskCompletionSource.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Then
        _mockPeerService.Verify(p => p.Disconnect(null), Times.Once);
    }

    [Fact]
    public async Task Given_PeerServiceFactoryThrowsException_When_CreatingConnectingPeer_Then_ExceptionIsLogged()
    {
        // Given
        var peerManager = CreatePeerManager();

        var mockTcpClient = new Mock<TcpClient>();
        var eventArgs = new NewPeerConnectedEventArgs(ExpectedHost, ExpectedPort, mockTcpClient.Object);

        _mockPeerServiceFactory
           .Setup(f => f.CreateConnectingPeerAsync(It.IsAny<TcpClient>()))
           .ThrowsAsync(new InvalidOperationException("Test factory error"));

        // When
        await peerManager.StartAsync(CancellationToken.None);
#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
        // ReSharper disable once MethodHasAsyncOverload
        _mockTcpService.Raise(t => t.OnNewPeerConnected += null, eventArgs);
#pragma warning restore CS8625 // Cannot convert null literal to non-nullable reference type.

        // Then
        _mockLogger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("Error handling new peer connection")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    private int _sendsStarted;
    private int _sendsInFlight;
    private int _maxConcurrentSends;

    private PeerManager CreatePeerManager()
    {
        return new PeerManager(_mockChannelManager.Object, _mockLogger.Object, _mockPeerServiceFactory.Object,
                               _mockTcpService.Object, _fakeServiceProvider);
    }

    /// <summary>
    /// Connects the mock peer the way the daemon does, so it gets its inbound loop and outbox.
    /// </summary>
    private async Task<PeerManager> CreatePeerManagerWithPeerAsync()
    {
        var peerManager = CreatePeerManager();
        var mockTcpClient = new Mock<TcpClient>();
        _mockTcpService.Setup(t => t.ConnectToPeerAsync(It.IsAny<PeerAddress>()))
                       .ReturnsAsync(new ConnectedPeer(_compactPubKey, ExpectedHost, ExpectedPort,
                                                       mockTcpClient.Object));
        await peerManager.ConnectToPeerAsync(new PeerAddressInfo($"{_compactPubKey}@127.0.0.1:9735"));
        return peerManager;
    }

    private void SetupChannelManagerReplies(params IChannelMessage[] replies)
    {
        _mockChannelManager
           .Setup(cm => cm.HandleChannelMessageAsync(It.IsAny<IChannelMessage>(), It.IsAny<FeatureOptions>(),
                                                     It.IsAny<CompactPubKey>()))
           .Callback(() =>
            {
                foreach (var reply in replies)
                    RaiseResponse(reply);
            })
           .ReturnsAsync(replies);
    }

    private void RaiseResponse(IChannelMessage message)
    {
        _mockChannelManager.Raise(cm => cm.OnResponseMessageReady += null, _mockChannelManager.Object,
                                  new ChannelResponseMessageEventArgs(_compactPubKey, message));
    }

    private void RaiseChannelMessage(IChannelMessage message)
    {
        _mockPeerService.Raise(p => p.OnChannelMessageReceived += null, _mockPeerService.Object,
                               new ChannelMessageEventArgs(message, _compactPubKey));
    }

    /// <summary>
    /// Records what the peer service sends, tracking how many sends overlap. The first send can be made slow, so
    /// anything enqueued meanwhile has to wait behind it.
    /// </summary>
    private Task<List<IChannelMessage>> CaptureSentMessages(int count, bool slowFirstSend = false)
    {
        var sent = new List<IChannelMessage>();
        var done = new TaskCompletionSource<List<IChannelMessage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockPeerService.Setup(p => p.SendMessageAsync(It.IsAny<IChannelMessage>()))
                        .Returns(async (IChannelMessage message) =>
                         {
                             var started = Interlocked.Increment(ref _sendsStarted);
                             var inFlight = Interlocked.Increment(ref _sendsInFlight);
                             InterlockedMax(ref _maxConcurrentSends, inFlight);
                             await Task.Delay(slowFirstSend && started == 1 ? 150 : 5);
                             Interlocked.Decrement(ref _sendsInFlight);
                             lock (sent)
                             {
                                 sent.Add(message);
                                 if (sent.Count == count)
                                     done.TrySetResult([.. sent]);
                             }
                         });
        return done.Task;
    }

    private TaskCompletionSource<Exception?> CaptureDisconnect()
    {
        var disconnectTcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockPeerService.Setup(p => p.Disconnect(It.IsAny<Exception?>()))
                        .Callback((Exception? e) => disconnectTcs.TrySetResult(e));
        return disconnectTcs;
    }

    private TaskCompletionSource<WarningException> CaptureWarning()
    {
        var warningTcs = new TaskCompletionSource<WarningException>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockPeerService.Setup(p => p.SendWarningAsync(It.IsAny<WarningException>()))
                        .Callback((WarningException w) => warningTcs.TrySetResult(w))
                        .Returns(Task.CompletedTask);
        return warningTcs;
    }

    private void SetupChannelMessage(MessageTypes messageType, ChannelId channelId)
    {
        var payloadMock = new Mock<IChannelMessagePayload>();
        payloadMock.SetupGet(p => p.ChannelId).Returns(channelId);
        _mockChannelMessage.SetupGet(m => m.Type).Returns(messageType);
        _mockChannelMessage.SetupGet(m => m.Payload).Returns(payloadMock.Object);
    }

    private static IChannelMessage CreateInboundMessage(ChannelId? channelId = null)
    {
        var payloadMock = new Mock<IChannelMessagePayload>();
        payloadMock.SetupGet(p => p.ChannelId).Returns(channelId ?? ChannelId.Zero);
        var messageMock = new Mock<IChannelMessage>();
        messageMock.SetupGet(m => m.Type).Returns(MessageTypes.ChannelReady);
        messageMock.SetupGet(m => m.Payload).Returns(payloadMock.Object);
        return messageMock.Object;
    }

    private static IChannelMessage[] CreateMessages(int count)
    {
        return Enumerable.Range(0, count).Select(_ => new Mock<IChannelMessage>().Object).ToArray();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + s_timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met in time");
            await Task.Delay(5);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current)
                return;
        } while (Interlocked.CompareExchange(ref target, value, current) != current);
    }

    private ChannelModel CreateChannel(ChannelState state, byte id)
    {
        var channelIdBytes = new byte[32];
        channelIdBytes[0] = id;
        var channelConfig = TestChannelParams.Create(LightningMoney.Zero, LightningMoney.Zero, LightningMoney.Zero,
                                              LightningMoney.Zero, 0, LightningMoney.Zero, 3, false,
                                              LightningMoney.Zero, 144, FeatureSupport.No);
        var keySet = new ChannelKeySetModel(0, _compactPubKey, _compactPubKey, _compactPubKey, _compactPubKey,
                                            _compactPubKey, _compactPubKey);

        return new ChannelModel(channelConfig, new ChannelId(channelIdBytes), null, null, false, null, null,
                                LightningMoney.Zero, keySet, 0, 0, LightningMoney.Zero, keySet, 0, _compactPubKey, 0,
                                state, ChannelVersion.V1);
    }
}
// ReSharper restore AccessToDisposedClosure