using Microsoft.Extensions.Options;
using NLightning.Infrastructure.Protocol.Models;
using NLightning.Tests.Utils;

namespace NLightning.Application.Tests.Node.Managers;

using Application.Node.Managers;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Node.ValueObjects;
using Infrastructure.Node.ValueObjects;

/// <summary>
/// NL-796: the deadline for the peer's <c>channel_reestablish</c> on a new connection, on a stepped clock.
/// </summary>
public partial class PeerManagerTests
{
    private static readonly TimeSpan s_reestablishTimeout = TimeSpan.FromSeconds(60);

    private static readonly ChannelId s_silentChannelId = new(Enumerable.Repeat((byte)0x79, 32).ToArray());

    [Fact]
    public void Given_NodeOptionsWithAReestablishTimeout_When_Constructed_Then_TheDeadlineUsesIt()
    {
        // Arrange
        var nodeOptions = Options.Create(new NodeOptions { ReestablishTimeout = TimeSpan.FromSeconds(7) });

        // Act
        var configured = new PeerManager(_mockChannelManager.Object, _mockChannelMemoryRepository.Object,
                                         _mockLogger.Object, _mockPeerServiceFactory.Object,
                                         _mockSecureKeyManager.Object, _mockTcpService.Object, _fakeServiceProvider,
                                         nodeOptions);
        var unconfigured = CreatePeerManager();

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(7), configured.ReestablishTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), unconfigured.ReestablishTimeout);
    }

    [Fact]
    public async Task Given_APeerThatNeverReestablishes_When_TheDeadlinePasses_Then_OneWarningClosesTheConnectionAndWeRedial()
    {
        // Arrange - an Open channel whose channel_reestablish went out and was never answered
        var clock = new SteppedClockProvider();
        _mockChannelManager.Setup(cm => cm.GetChannelsAwaitingPeerReestablish(_compactPubKey))
                           .Returns([s_silentChannelId]);
        var disconnects = new List<Exception?>();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockPeerService.Setup(p => p.Disconnect(It.IsAny<Exception?>()))
                        .Callback((Exception? e) =>
                         {
                             lock (disconnects)
                                 disconnects.Add(e);
                             disconnected.TrySetResult();
                         });
        var peerManager = await CreatePeerManagerWithReestablishDeadlineAsync(clock);
        peerManager.ReconnectInitialDelay = TimeSpan.FromMilliseconds(10);
        await peerManager.StartAsync(TestContext.Current.CancellationToken);
        _mockChannelMemoryRepository.Setup(r => r.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
                                    .Returns([CreateChannel(ChannelState.Open, 1)]);
        await WaitUntilAsync(() => clock.PendingTimers == 1);

        // Act - just before the deadline nothing is checked
        clock.Advance(s_reestablishTimeout - TimeSpan.FromSeconds(1));

        // Assert
        _mockChannelManager.Verify(cm => cm.GetChannelsAwaitingPeerReestablish(It.IsAny<CompactPubKey>()),
                                   Times.Never);
        Assert.NotNull(peerManager.GetPeer(_compactPubKey));

        // Act - the deadline passes
        clock.Advance(TimeSpan.FromSeconds(1));
        await disconnected.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Assert - one warning (not an error: the channel is not failed) closes the connection
        Exception? reason;
        lock (disconnects)
            reason = Assert.Single(disconnects);
        var warning = Assert.IsType<WarningException>(reason);
        Assert.Contains("channel_reestablish", warning.Message);
        _mockChannelManager.Verify(cm => cm.GetChannelsAwaitingPeerReestablish(_compactPubKey), Times.Once);

        // Act - the transport reports the close
        RaiseDisconnect(_mockPeerService);

        // Assert - not a disconnect on purpose: the reconnect backoff dials the peer again, and the new connection
        // gets its own deadline
        await WaitUntilAsync(() => peerManager.GetPeer(_compactPubKey) is not null && clock.PendingTimers == 1);
        _mockTcpService.Verify(t => t.ConnectToPeerAsync(It.IsAny<PeerAddress>()), Times.Exactly(2));
        lock (disconnects)
            Assert.Single(disconnects);
    }

    [Fact]
    public async Task Given_EveryChannelReestablished_When_TheDeadlinePasses_Then_TheConnectionStays()
    {
        // Arrange
        var clock = new SteppedClockProvider();
        _mockChannelManager.Setup(cm => cm.GetChannelsAwaitingPeerReestablish(_compactPubKey)).Returns([]);
        var peerManager = await CreatePeerManagerWithReestablishDeadlineAsync(clock);
        await WaitUntilAsync(() => clock.PendingTimers == 1);

        // Act
        clock.Advance(s_reestablishTimeout);

        // Assert - checked once, nothing queued: the connection stays and no timer is left
        _mockChannelManager.Verify(cm => cm.GetChannelsAwaitingPeerReestablish(_compactPubKey), Times.Once);
        _mockPeerService.Verify(p => p.Disconnect(It.IsAny<Exception?>()), Times.Never);
        Assert.NotNull(peerManager.GetPeer(_compactPubKey));
        Assert.Equal(0, clock.PendingTimers);
    }

    [Fact]
    public async Task Given_TheConnectionDropped_When_ItsDeadlineWouldPass_Then_NothingIsChecked()
    {
        // Arrange - the deadline belongs to the connection it was armed on
        var clock = new SteppedClockProvider();
        _mockChannelManager.Setup(cm => cm.GetChannelsAwaitingPeerReestablish(_compactPubKey))
                           .Returns([s_silentChannelId]);
        var peerManager = await CreatePeerManagerWithReestablishDeadlineAsync(clock);
        await WaitUntilAsync(() => clock.PendingTimers == 1);

        // Act
        RaiseDisconnect(_mockPeerService);
        clock.Advance(s_reestablishTimeout);

        // Assert
        Assert.Null(peerManager.GetPeer(_compactPubKey));
        Assert.Equal(0, clock.PendingTimers);
        _mockChannelManager.Verify(cm => cm.GetChannelsAwaitingPeerReestablish(It.IsAny<CompactPubKey>()),
                                   Times.Never);
        _mockPeerService.Verify(p => p.Disconnect(It.IsAny<Exception?>()), Times.Never);
    }

    [Fact]
    public async Task Given_AZeroReestablishTimeout_When_APeerConnects_Then_NoDeadlineIsArmed()
    {
        // Arrange
        var clock = new SteppedClockProvider();
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockChannelManager
           .Setup(cm => cm.HandleChannelMessageAsync(_mockChannelMessage.Object, It.IsAny<FeatureOptions>(),
                                                     It.IsAny<CompactPubKey>()))
           .Callback(() => handled.TrySetResult())
           .Returns(Task.CompletedTask);

        // Act - the inbound loop handles a message only after the reestablish start (and the arming) finished
        var peerManager = await CreatePeerManagerWithReestablishDeadlineAsync(clock, TimeSpan.Zero);
        RaiseChannelMessage(_mockChannelMessage.Object);
        await handled.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        clock.Advance(s_reestablishTimeout);

        // Assert
        Assert.Equal(0, clock.PendingTimers);
        Assert.NotNull(peerManager.GetPeer(_compactPubKey));
        _mockChannelManager.Verify(cm => cm.GetChannelsAwaitingPeerReestablish(It.IsAny<CompactPubKey>()),
                                   Times.Never);
    }

    [Fact]
    public async Task Given_ADeadlineThatCannotBeArmed_When_APeerConnects_Then_ItsMessagesAreStillHandled()
    {
        // Arrange - NL-891: the timer refuses the deadline (a timer's own limit); the inbound loop must go on
        _fakeServiceProvider.AddService(typeof(TimeProvider), new ThrowingTimerProvider());
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockChannelManager
           .Setup(cm => cm.HandleChannelMessageAsync(_mockChannelMessage.Object, It.IsAny<FeatureOptions>(),
                                                     It.IsAny<CompactPubKey>()))
           .Callback(() => handled.TrySetResult())
           .Returns(Task.CompletedTask);
        var peerManager = CreatePeerManager();
        peerManager.ReestablishTimeout = s_reestablishTimeout;
        _mockTcpService.Setup(t => t.ConnectToPeerAsync(It.IsAny<PeerAddress>()))
                       .ReturnsAsync(() => new ConnectedPeer(_compactPubKey, ExpectedHost, ExpectedPort,
                                                             new Mock<System.Net.Sockets.TcpClient>().Object));
        await peerManager.ConnectToPeerAsync(new PeerAddressInfo($"{_compactPubKey}@127.0.0.1:9735"));

        // Act
        RaiseChannelMessage(_mockChannelMessage.Object);
        await handled.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Assert - the message went through and the connection stays
        Assert.NotNull(peerManager.GetPeer(_compactPubKey));
        _mockPeerService.Verify(p => p.Disconnect(It.IsAny<Exception?>()), Times.Never);
    }

    /// <summary>A clock whose timers cannot be created, as <see cref="TimeProvider.System"/> past its limit.</summary>
    private sealed class ThrowingTimerProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            throw new ArgumentOutOfRangeException(nameof(dueTime));
    }

    /// <summary>
    /// Connects the mock peer to a manager whose deadline runs on <paramref name="clock"/>.
    /// </summary>
    private async Task<PeerManager> CreatePeerManagerWithReestablishDeadlineAsync(SteppedClockProvider clock,
                                                                                TimeSpan? timeout = null)
    {
        _fakeServiceProvider.AddService(typeof(TimeProvider), clock);
        var peerManager = CreatePeerManager();
        peerManager.ReestablishTimeout = timeout ?? s_reestablishTimeout;
        _mockTcpService.Setup(t => t.ConnectToPeerAsync(It.IsAny<PeerAddress>()))
                       .ReturnsAsync(() => new ConnectedPeer(_compactPubKey, ExpectedHost, ExpectedPort,
                                                             new Mock<System.Net.Sockets.TcpClient>().Object));
        await peerManager.ConnectToPeerAsync(new PeerAddressInfo($"{_compactPubKey}@127.0.0.1:9735"));
        return peerManager;
    }
}