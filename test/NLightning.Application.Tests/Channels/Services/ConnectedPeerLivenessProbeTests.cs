using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Channels.Services;

using Application.Channels.Services;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using static Handlers.NormalOperationTestContext;

/// <summary>
/// The channel link of <see cref="ConnectedPeerLivenessProbe"/>: pinned to the peer's connection (its
/// <see cref="PeerModel"/> instance) when the channel is marked up, down after a reconnection until marked again.
/// </summary>
public class ConnectedPeerLivenessProbeTests
{
    private readonly Mock<IPeerManager> _peerManager = new();
    private PeerModel? _connection;

    public ConnectedPeerLivenessProbeTests()
    {
        _peerManager.Setup(m => m.GetPeer(PeerNodeId)).Returns(() => _connection);
    }

    [Fact]
    public async Task Given_ChannelNeverMarked_When_Probed_Then_NotAlive()
    {
        // Arrange - a channel loaded at startup (no channel_reestablish yet) with its peer connected
        _connection = NewConnection();
        var probe = CreateProbe();

        // Act
        var alive = await probe.IsAliveAsync(TestChannelId, PeerNodeId, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(alive);
    }

    [Fact]
    public async Task Given_ChannelMarkedOnTheCurrentConnection_When_Probed_Then_Alive()
    {
        // Arrange
        _connection = NewConnection();
        var probe = CreateProbe();
        probe.MarkLinkUp(TestChannelId, PeerNodeId);

        // Act
        var alive = await probe.IsAliveAsync(TestChannelId, PeerNodeId, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(alive);
    }

    [Fact]
    public async Task Given_PeerDisconnected_When_Probed_Then_NotAlive()
    {
        // Arrange
        _connection = NewConnection();
        var probe = CreateProbe();
        probe.MarkLinkUp(TestChannelId, PeerNodeId);
        _connection = null;

        // Act
        var alive = await probe.IsAliveAsync(TestChannelId, PeerNodeId, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(alive);
    }

    [Fact]
    public async Task Given_PeerReconnected_When_Probed_Then_NotAliveUntilMarkedAgain()
    {
        // Arrange - BOLT 2: nothing but channel_reestablish may go out on the new connection
        _connection = NewConnection();
        var probe = CreateProbe();
        probe.MarkLinkUp(TestChannelId, PeerNodeId);
        _connection = NewConnection();

        // Act
        var beforeReestablish = await probe.IsAliveAsync(TestChannelId, PeerNodeId,
                                                         TestContext.Current.CancellationToken);
        probe.MarkLinkUp(TestChannelId, PeerNodeId);
        var afterReestablish = await probe.IsAliveAsync(TestChannelId, PeerNodeId,
                                                        TestContext.Current.CancellationToken);

        // Assert
        Assert.False(beforeReestablish);
        Assert.True(afterReestablish);
    }

    [Fact]
    public async Task Given_MarkedWhileDisconnected_When_Reconnected_Then_NotAlive()
    {
        // Arrange - marking without a connection pins nothing
        var probe = CreateProbe();
        probe.MarkLinkUp(TestChannelId, PeerNodeId);
        _connection = NewConnection();

        // Act
        var alive = await probe.IsAliveAsync(TestChannelId, PeerNodeId, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(alive);
    }

    [Fact]
    public async Task Given_NoPeerManager_When_Probed_Then_OnlyMarkedChannelsAreAlive()
    {
        // Arrange - in-process tests
        var probe = new ConnectedPeerLivenessProbe(new ServiceCollection().BuildServiceProvider());

        // Act
        var before = await probe.IsAliveAsync(TestChannelId, PeerNodeId, TestContext.Current.CancellationToken);
        probe.MarkLinkUp(TestChannelId, PeerNodeId);
        var after = await probe.IsAliveAsync(TestChannelId, PeerNodeId, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(before);
        Assert.True(after);
    }

    private ConnectedPeerLivenessProbe CreateProbe()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_peerManager.Object);
        return new ConnectedPeerLivenessProbe(services.BuildServiceProvider());
    }

    private static PeerModel NewConnection() => new(PeerNodeId, "127.0.0.1", 9735, "tcp");
}