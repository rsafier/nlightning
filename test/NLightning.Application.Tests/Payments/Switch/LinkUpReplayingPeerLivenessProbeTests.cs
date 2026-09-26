using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Payments.Switch;

using Application.Channels.Interfaces;
using Application.Channels.Reestablish;
using Application.Channels.Services;
using Application.Payments.Switch;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using static Channels.Handlers.NormalOperationTestContext;

/// <summary>
/// <see cref="LinkUpReplayingPeerLivenessProbe"/>: the pending events of a channel reach the switch once per link-up
/// (NL-264). After a channel_reestablish <c>ChannelManager</c> replays them itself, so the probe must not.
/// </summary>
public class LinkUpReplayingPeerLivenessProbeTests
{
    private readonly Mock<IPeerLivenessProbe> _inner = new();
    private readonly Mock<IChannelLockProvider> _locks = new();
    private readonly Mock<IChannelMemoryRepository> _channels = new();
    private readonly ReestablishTracker _tracker = new();

    public LinkUpReplayingPeerLivenessProbeTests()
    {
        _locks.Setup(l => l.AcquireAsync(It.IsAny<ChannelId>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new Mock<IDisposable>().Object);
        ChannelModel? none = null;
        _channels.Setup(c => c.TryGetChannel(It.IsAny<ChannelId>(), out none)).Returns(false);
    }

    [Fact]
    public async Task Given_AChannelJustReestablished_When_MarkingItsLinkUp_Then_NoSecondReplayIsScheduled()
    {
        // Arrange - the order of ChannelManager.CompleteReestablishAsync: tracker first, then MarkLinkUp
        var (probe, replayer) = CreateProbe(_tracker);
        _tracker.MarkSent(TestChannelId, PeerNodeId);
        Assert.True(_tracker.TryMarkReestablished(TestChannelId));

        // Act
        probe.MarkLinkUp(TestChannelId, PeerNodeId);
        await replayer.WhenIdleAsync();

        // Assert - the link is marked, the events are left to the manager
        _inner.Verify(p => p.MarkLinkUp(TestChannelId, PeerNodeId), Times.Once);
        _locks.Verify(l => l.AcquireAsync(It.IsAny<ChannelId>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Given_AChannelThatTurnedOpenOnThisConnection_When_MarkingItsLinkUp_Then_TheReplayIsScheduled()
    {
        // Arrange - channel_ready starts normal operation: nobody else replays for it
        var (probe, replayer) = CreateProbe(_tracker);
        _tracker.MarkOpened(TestChannelId, PeerNodeId);

        // Act
        probe.MarkLinkUp(TestChannelId, PeerNodeId);
        await replayer.WhenIdleAsync();

        // Assert
        _inner.Verify(p => p.MarkLinkUp(TestChannelId, PeerNodeId), Times.Once);
        _locks.Verify(l => l.AcquireAsync(TestChannelId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_NoTracker_When_MarkingALinkUp_Then_TheReplayIsScheduled()
    {
        // Arrange - hosts without reestablish (in-process harnesses) keep the replay on every link-up
        var (probe, replayer) = CreateProbe(null);

        // Act
        probe.MarkLinkUp(TestChannelId, PeerNodeId);
        await replayer.WhenIdleAsync();

        // Assert
        _locks.Verify(l => l.AcquireAsync(TestChannelId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_TheApplicationRegistrations_When_AReestablishedChannelIsMarkedUp_Then_ItIsNotReplayedTwice()
    {
        // Arrange - the production order: reestablish, operations, then the switch decorator
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_inner.Object);
        services.AddReestablishServices();
        services.AddChannelOperationsServices();
        services.AddSingleton(_locks.Object);
        services.AddSingleton(_channels.Object);
        services.AddHtlcSwitchServices();
        await using var provider = services.BuildServiceProvider();
        var tracker = provider.GetRequiredService<ReestablishTracker>();
        var probe = provider.GetRequiredService<IPeerLivenessProbe>();
        tracker.MarkSent(TestChannelId, PeerNodeId);
        tracker.TryMarkReestablished(TestChannelId);

        // Act
        probe.MarkLinkUp(TestChannelId, PeerNodeId);
        await provider.GetRequiredService<LinkUpEventReplayer>().WhenIdleAsync();

        // Assert
        Assert.IsType<LinkUpReplayingPeerLivenessProbe>(probe);
        _locks.Verify(l => l.AcquireAsync(It.IsAny<ChannelId>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private (LinkUpReplayingPeerLivenessProbe Probe, LinkUpEventReplayer Replayer) CreateProbe(
        ReestablishTracker? tracker)
    {
        var replayer = new LinkUpEventReplayer(_locks.Object, _channels.Object,
                                               NullLogger<LinkUpEventReplayer>.Instance,
                                               new ServiceCollection().BuildServiceProvider());
        return (new LinkUpReplayingPeerLivenessProbe(_inner.Object, replayer, tracker), replayer);
    }
}