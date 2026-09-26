using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Channels.Services;

using Application.Channels.Services;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using static Handlers.NormalOperationTestContext;

/// <summary>
/// <see cref="PingBeforeCommit"/> (BOLT 2 B2-CS-S05, NL-251): a quiet peer is pinged, and its pong awaited, before a
/// <c>commitment_signed</c>.
/// </summary>
public class PingBeforeCommitTests
{
    private static readonly DateTimeOffset s_now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly Mock<IPeerManager> _peerManager = new();
    private readonly Mock<IPeerService> _peerService = new();
    private readonly CommitSchedulerOptions _options = new()
    {
        PingWhenQuietFor = TimeSpan.FromSeconds(30),
        PongTimeout = TimeSpan.FromSeconds(7)
    };

    public PingBeforeCommitTests()
    {
        var peer = new PeerModel(PeerNodeId, "127.0.0.1", 9735, "IPv4");
        peer.SetPeerService(_peerService.Object);
        _peerManager.Setup(m => m.GetPeer(PeerNodeId)).Returns(peer);
    }

    [Fact]
    public async Task Given_AMessageReceivedRecently_When_EnsuringResponsive_Then_NoPingIsSent()
    {
        // Arrange
        _peerService.SetupGet(p => p.LastMessageReceivedAt).Returns(s_now.AddSeconds(-5));
        var check = CreateCheck(_peerManager.Object);

        // Act
        var responsive = await check.EnsureResponsiveAsync(PeerNodeId, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(responsive);
        _peerService.Verify(p => p.PingAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Given_AQuietPeerThatAnswers_When_EnsuringResponsive_Then_ItIsPingedWithThePongTimeout()
    {
        // Arrange
        _peerService.SetupGet(p => p.LastMessageReceivedAt).Returns(s_now.AddSeconds(-31));
        _peerService.Setup(p => p.PingAsync(_options.PongTimeout, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var check = CreateCheck(_peerManager.Object);

        // Act
        var responsive = await check.EnsureResponsiveAsync(PeerNodeId, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(responsive);
        _peerService.Verify(p => p.PingAsync(_options.PongTimeout, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_NothingReceivedYet_When_EnsuringResponsive_Then_ItIsPinged()
    {
        // Arrange
        _peerService.SetupGet(p => p.LastMessageReceivedAt).Returns((DateTimeOffset?)null);
        _peerService.Setup(p => p.PingAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var check = CreateCheck(_peerManager.Object);

        // Act
        var responsive = await check.EnsureResponsiveAsync(PeerNodeId, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(responsive);
        _peerService.Verify(p => p.PingAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_AQuietPeerThatDoesNotAnswer_When_EnsuringResponsive_Then_False()
    {
        // Arrange
        _peerService.SetupGet(p => p.LastMessageReceivedAt).Returns(s_now.AddMinutes(-10));
        _peerService.Setup(p => p.PingAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var check = CreateCheck(_peerManager.Object);

        // Act
        var responsive = await check.EnsureResponsiveAsync(PeerNodeId, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(responsive);
    }

    [Fact]
    public async Task Given_APeerThatIsNotConnected_When_EnsuringResponsive_Then_False()
    {
        // Arrange
        var check = CreateCheck(_peerManager.Object);

        // Act
        var responsive = await check.EnsureResponsiveAsync(Point(0x77), TestContext.Current.CancellationToken);

        // Assert
        Assert.False(responsive);
    }

    [Fact]
    public async Task Given_NoPeerManager_When_EnsuringResponsive_Then_True()
    {
        // Arrange - in-process harnesses have no peer manager
        var check = CreateCheck(null);

        // Act
        var responsive = await check.EnsureResponsiveAsync(PeerNodeId, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(responsive);
    }

    private PingBeforeCommit CreateCheck(IPeerManager? peerManager)
    {
        var services = new ServiceCollection();
        if (peerManager is not null)
            services.AddSingleton(peerManager);

        return new PingBeforeCommit(NullLogger<PingBeforeCommit>.Instance, services.BuildServiceProvider(),
                                    Options.Create(_options), new FixedTimeProvider(s_now));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}