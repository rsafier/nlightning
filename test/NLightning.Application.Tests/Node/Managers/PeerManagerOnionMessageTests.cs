using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NLightning.Infrastructure.Protocol.Models;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Node.Managers;

using Application.Node.Managers;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Node.Events;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.Options;
using Domain.Node.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.OnionMessages.Interfaces;
using Domain.Protocol.Payloads;
using Infrastructure.Node.ValueObjects;
using Infrastructure.Transport.Interfaces;

/// <summary>
/// Wave M6 (OM2-T1): <see cref="PeerManager"/> as <see cref="IPeerOnionMessageOutbox"/> queues onion messages only for
/// a connected peer that negotiated <c>option_onion_messages</c>, on that connection's bounded outbox, never connects,
/// and releases the peer's <see cref="IOnionMessageRateLimiter"/> bucket when it disconnects.
/// </summary>
public class PeerManagerOnionMessageTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    private static readonly CompactPubKey s_lowerNodeKey =
        new PubKey("023da092f6980e58d2c037173180e9a465476026ee50f96695963e8efe436f54eb").ToBytes();

    private readonly CompactPubKey _peerId =
        new PubKey("028d7500dd4c12685d1f568b4c2b5048e8534b873319f3a8daa612b469132ec7f7").ToBytes();

    private readonly Mock<IChannelManager> _channelManager = new();
    private readonly Mock<IPeerServiceFactory> _peerServiceFactory = new();
    private readonly Mock<IPeerService> _peerService = new();
    private readonly Mock<ITcpService> _tcpService = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IPeerDbRepository> _peerDbRepository = new();
    private readonly Mock<IChannelMemoryRepository> _channelMemoryRepository = new();
    private readonly Mock<ISecureKeyManager> _secureKeyManager = new();
    private readonly Mock<IOnionMessageRateLimiter> _rateLimiter = new();
    private readonly FakeServiceProvider _serviceProvider = new();

    public PeerManagerOnionMessageTests()
    {
        _peerService.SetupGet(p => p.PeerPubKey).Returns(_peerId);
        _peerService.SetupGet(p => p.Features)
                    .Returns(new FeatureOptions { OptionOnionMessages = FeatureSupport.Optional });
        _peerService.Setup(p => p.SendMessageAsync(It.IsAny<IChannelMessage>())).Returns(Task.CompletedTask);
        _peerService.Setup(p => p.SendWarningAsync(It.IsAny<WarningException>())).Returns(Task.CompletedTask);
        _peerServiceFactory.Setup(f => f.CreateConnectedPeerAsync(It.IsAny<CompactPubKey>(), It.IsAny<TcpClient>()))
                           .ReturnsAsync(_peerService.Object);
        _tcpService.Setup(t => t.ConnectToPeerAsync(It.IsAny<PeerAddress>()))
                   .ReturnsAsync(new ConnectedPeer(_peerId, "127.0.0.1", 9735, new Mock<TcpClient>().Object));

        _unitOfWork.Setup(u => u.PeerDbRepository).Returns(_peerDbRepository.Object);
        _unitOfWork.Setup(u => u.GetPeersForStartupAsync()).ReturnsAsync(() => []);
        _peerDbRepository.Setup(r => r.AddOrUpdateAsync(It.IsAny<PeerModel>())).Returns(Task.CompletedTask);
        _serviceProvider.AddService(typeof(IUnitOfWork), _unitOfWork.Object);

        _channelMemoryRepository.Setup(r => r.FindChannels(It.IsAny<Func<ChannelModel, bool>>())).Returns([]);
        _secureKeyManager.Setup(k => k.GetNodePubKey()).Returns(s_lowerNodeKey);
    }

    [Fact]
    public async Task Given_AConnectedPeerWithOnionMessages_When_TryEnqueueOnionMessage_Then_ItIsSentOnItsConnection()
    {
        // Arrange
        var sent = new TaskCompletionSource<OnionMessageMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _peerService.Setup(p => p.SendOnionMessageAsync(It.IsAny<OnionMessageMessage>(), It.IsAny<CancellationToken>()))
                    .Callback((OnionMessageMessage m, CancellationToken _) => sent.TrySetResult(m))
                    .Returns(Task.CompletedTask);
        var peerManager = await CreateConnectedPeerManagerAsync();
        var message = CreateOnionMessage();

        // Act
        var canSend = peerManager.CanSendOnionMessage(_peerId);
        var queued = peerManager.TryEnqueueOnionMessage(_peerId, message);

        // Assert
        Assert.True(canSend);
        Assert.True(queued);
        Assert.Same(message, await sent.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_APeerWithoutOnionMessages_When_TryEnqueueOnionMessage_Then_NothingIsQueued()
    {
        // Arrange
        _peerService.SetupGet(p => p.Features).Returns(new FeatureOptions { OptionOnionMessages = FeatureSupport.No });
        var peerManager = await CreateConnectedPeerManagerAsync();

        // Act
        var canSend = peerManager.CanSendOnionMessage(_peerId);
        var queued = peerManager.TryEnqueueOnionMessage(_peerId, CreateOnionMessage());

        // Assert
        Assert.False(canSend);
        Assert.False(queued);
        Assert.Equal(0, peerManager.QueuedOutboxOnionMessageCount);
    }

    [Fact]
    public void Given_AnUnknownPeer_When_TryEnqueueOnionMessage_Then_FalseAndNoConnectionIsOpened()
    {
        // Arrange: BOLT12 plan D6, we never connect to forward an onion message
        var peerManager = CreatePeerManager();

        // Act
        var canSend = peerManager.CanSendOnionMessage(_peerId);
        var queued = peerManager.TryEnqueueOnionMessage(_peerId, CreateOnionMessage());

        // Assert
        Assert.False(canSend);
        Assert.False(queued);
        _tcpService.Verify(t => t.ConnectToPeerAsync(It.IsAny<PeerAddress>()), Times.Never);
    }

    [Fact]
    public async Task Given_ASlowPeer_When_MoreThanTheCapIsQueued_Then_TheRestIsDropped()
    {
        // Arrange: the peer's first send never completes
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _peerService.Setup(p => p.SendOnionMessageAsync(It.IsAny<OnionMessageMessage>(), It.IsAny<CancellationToken>()))
                    .Returns(() =>
                     {
                         started.TrySetResult();
                         return release.Task;
                     });
        var peerManager = CreatePeerManager();
        peerManager.MaxOutboxOnionMessagesPerPeer = 2;
        await ConnectAsync(peerManager);
        Assert.True(peerManager.TryEnqueueOnionMessage(_peerId, CreateOnionMessage()));
        await started.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Act
        var results = Enumerable.Range(0, 3).Select(_ => peerManager.TryEnqueueOnionMessage(_peerId,
                                                                                         CreateOnionMessage()))
                                .ToArray();

        // Assert
        Assert.Equal([true, true, false], results);
        Assert.Equal(2, peerManager.QueuedOutboxOnionMessageCount);
        release.SetResult();
    }

    [Fact]
    public async Task Given_AConnectedPeer_When_ItDisconnects_Then_ItsRateLimitIsReleasedAndNothingMoreIsQueued()
    {
        // Arrange
        _serviceProvider.AddService(typeof(IOnionMessageRateLimiter), _rateLimiter.Object);
        var peerManager = await CreateConnectedPeerManagerAsync();

        // Act
        _peerService.Raise(p => p.OnDisconnect += null, _peerService.Object, new PeerDisconnectedEventArgs(_peerId));

        // Assert
        _rateLimiter.Verify(l => l.RemovePeer(_peerId), Times.Once);
        Assert.False(peerManager.CanSendOnionMessage(_peerId));
        Assert.False(peerManager.TryEnqueueOnionMessage(_peerId, CreateOnionMessage()));
    }

    [Fact]
    public async Task Given_NoRateLimiter_When_APeerDisconnects_Then_NothingFails()
    {
        // Arrange: no IOnionMessageRateLimiter registered
        var peerManager = await CreateConnectedPeerManagerAsync();

        // Act
        _peerService.Raise(p => p.OnDisconnect += null, _peerService.Object, new PeerDisconnectedEventArgs(_peerId));

        // Assert
        Assert.Null(peerManager.GetPeer(_peerId));
    }

    [Fact]
    public async Task Given_ANullMessage_When_TryEnqueueOnionMessage_Then_False()
    {
        // Arrange
        var peerManager = await CreateConnectedPeerManagerAsync();

        // Act & Assert
        Assert.False(peerManager.TryEnqueueOnionMessage(_peerId, null!));
    }

    private PeerManager CreatePeerManager() =>
        new(_channelManager.Object, _channelMemoryRepository.Object, new Mock<ILogger<PeerManager>>().Object,
            _peerServiceFactory.Object, _secureKeyManager.Object, _tcpService.Object, _serviceProvider);

    private async Task<PeerManager> CreateConnectedPeerManagerAsync()
    {
        var peerManager = CreatePeerManager();
        await ConnectAsync(peerManager);
        return peerManager;
    }

    private async Task ConnectAsync(PeerManager peerManager)
    {
        await peerManager.ConnectToPeerAsync(new PeerAddressInfo($"{_peerId}@127.0.0.1:9735"));
        Assert.NotNull(peerManager.GetPeer(_peerId));
    }

    private static OnionMessageMessage CreateOnionMessage()
    {
        var pathKey = new byte[33];
        pathKey[0] = 0x02;
        return new OnionMessageMessage(new OnionMessagePayload(new CompactPubKey(pathKey), new byte[1366]));
    }
}