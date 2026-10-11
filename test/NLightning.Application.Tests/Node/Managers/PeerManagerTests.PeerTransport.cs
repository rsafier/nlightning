using System.Net;

namespace NLightning.Application.Tests.Node.Managers;

using Application.Gossip.Graph.Interfaces;
using Domain.Node.Models;
using Domain.Transport;
using Infrastructure.Transport.Events;

/// <summary>
/// Inbound connections a host handed over (NL-1369, <c>IPeerInboundAcceptor</c>): a proxied one is saved like a
/// loopback one (inbound-only, NL-497), never at the endpoint the host reported, unless the peer announced where it
/// listens (whether Tor is on or not); a stream without an endpoint is saved inbound-only too.
/// </summary>
public partial class PeerManagerTests
{
    [Fact]
    public async Task Given_AProxiedInboundPeerFromAPublicAddress_When_Connected_Then_SavedAsInboundOnly()
    {
        // Arrange: the same host saved dialable without the proxy flag (Given_AnInboundPeerFromAnotherAddress_*)
        var peerManager = CreatePeerManager();
        await peerManager.StartAsync(TestContext.Current.CancellationToken);
        PeerModel? saved = null;
        _mockPeerDbRepository.Setup(r => r.AddOrUpdateAsync(It.IsAny<PeerModel>()))
                             .Callback((PeerModel peer) => saved = peer)
                             .Returns(Task.CompletedTask);

        // Act
        RaiseHandedOverConnection(new IPEndPoint(IPAddress.Parse(RemoteHost), 41000), isProxied: true);
        await WaitUntilAsync(() => saved is not null);
        await peerManager.StopAsync();

        // Assert
        Assert.True(saved!.IsInboundOnly);
        Assert.Equal((string.Empty, 0U), (saved.Host, saved.Port));
    }

    [Fact]
    public async Task Given_AProxiedInboundPeerWithAnAnnouncedAddress_When_Connected_Then_SavedAtTheAnnouncedAddress()
    {
        // Arrange: Tor is off; the announcement still says where the peer listens
        var announced = AnnouncedNode(_compactPubKey, "203.0.113.7", 9736);
        var graphStore = new Mock<IGraphStore>();
        graphStore.Setup(g => g.TryGetNode(_compactPubKey, out announced)).Returns(true);
        _fakeServiceProvider.AddService(typeof(IGraphStore), graphStore.Object);
        var peerManager = CreatePeerManager();
        await peerManager.StartAsync(TestContext.Current.CancellationToken);
        PeerModel? saved = null;
        _mockPeerDbRepository.Setup(r => r.AddOrUpdateAsync(It.IsAny<PeerModel>()))
                             .Callback((PeerModel peer) => saved = peer)
                             .Returns(Task.CompletedTask);

        // Act
        RaiseHandedOverConnection(new IPEndPoint(IPAddress.Loopback, 41000), isProxied: true);
        await WaitUntilAsync(() => saved is not null);
        await peerManager.StopAsync();

        // Assert
        Assert.False(saved!.IsInboundOnly);
        Assert.Equal(("203.0.113.7", 9736U), (saved.Host, saved.Port));
    }

    [Fact]
    public async Task Given_AHandedOverStreamWithoutAnEndPoint_When_Connected_Then_SavedAsInboundOnly()
    {
        // Arrange
        var peerManager = CreatePeerManager();
        await peerManager.StartAsync(TestContext.Current.CancellationToken);
        PeerModel? saved = null;
        _mockPeerDbRepository.Setup(r => r.AddOrUpdateAsync(It.IsAny<PeerModel>()))
                             .Callback((PeerModel peer) => saved = peer)
                             .Returns(Task.CompletedTask);

        // Act: not proxied, but nothing says where it came from
        RaiseHandedOverConnection(null, isProxied: false);
        await WaitUntilAsync(() => saved is not null);
        await peerManager.StopAsync();

        // Assert
        Assert.True(saved!.IsInboundOnly);
        Assert.Equal(string.Empty, saved.Host);
    }

    /// <summary>What <c>TcpService.AcceptInboundAsync</c> raises for a stream a host handed over.</summary>
    private void RaiseHandedOverConnection(IPEndPoint? remoteEndPoint, bool isProxied)
    {
        var connection = PeerConnection.FromStream(new MemoryStream(), remoteEndPoint, isProxied);
        _mockTcpService.Raise(t => t.OnNewPeerConnected += null, _mockTcpService.Object,
                              new NewPeerConnectedEventArgs(remoteEndPoint?.Address.ToString() ?? string.Empty,
                                                            (uint)(remoteEndPoint?.Port ?? 0), connection));
    }
}