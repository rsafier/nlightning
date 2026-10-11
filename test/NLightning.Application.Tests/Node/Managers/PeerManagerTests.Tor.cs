using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using NBitcoin;
using NLightning.Infrastructure.Protocol.Models;

namespace NLightning.Application.Tests.Node.Managers;

using Application.Gossip.Graph.Interfaces;
using Application.Node.Managers;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Exceptions;
using Domain.Gossip.Addresses;
using Domain.Gossip.Graph;
using Domain.Node.Models;
using Domain.Node.Options;
using Infrastructure.Node.ValueObjects;

/// <summary>
/// The startup dials of stored peers (NL-576) and inbound peers that reached our onion service (NL-579).
/// </summary>
public partial class PeerManagerTests
{
    private const string PeerOnionHost = "duckduckgogg42xjoc72x3sjasowoarfbgcmvfimaftt6twagswzczad.onion";

    [Fact]
    public async Task Given_StoredPeersThatDoNotAnswer_When_StartAsync_Then_ItReturnsAfterTheStartupWaitAndListens()
    {
        // Arrange - NL-576: three offline peers with channels, each dial failing only after 3 s (an onion rendezvous
        // that never completes); dialed one after the other they held the start (and the chain monitor after it) 9 s
        var peerManager = CreatePeerManager();
        peerManager.StartupDialWait = TimeSpan.FromMilliseconds(200);
        peerManager.ReconnectInitialDelay = TimeSpan.FromMinutes(1);
        var peers = Enumerable.Range(1, 3)
                              .Select(i => new PeerModel(new Key().PubKey.ToBytes(), "203.0.113.7", 9735, "IPv4")
                              {
                                  Channels = [CreateChannel(ChannelState.Open, (byte)i)]
                              })
                              .ToList();
        _mockUnitOfWork.Setup(u => u.GetPeersForStartupAsync()).ReturnsAsync(peers);
        var dials = 0;
        _mockTcpService.Setup(t => t.ConnectToPeerAsync(It.IsAny<PeerAddress>()))
                       .Returns(async () =>
                        {
                            Interlocked.Increment(ref dials);
                            await Task.Delay(TimeSpan.FromSeconds(3));
                            throw new ConnectionException("Timeout connecting to peer through Tor");
                        });
        var stopwatch = Stopwatch.StartNew();

        // Act
        await peerManager.StartAsync(TestContext.Current.CancellationToken);
        var started = stopwatch.Elapsed;

        // Assert: every dial started at once, the start did not wait for them, and the listener is up
        Assert.Equal(3, Volatile.Read(ref dials));
        Assert.True(started < TimeSpan.FromSeconds(2), $"StartAsync took {started}");
        _mockTcpService.Verify(t => t.StartListeningAsync(It.IsAny<CancellationToken>()), Times.Once);

        // Act: stopping cancels the dials still running
        stopwatch.Restart();
        await peerManager.StopAsync();

        // Assert
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"StopAsync took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Given_ManyDeadPeersListedFirst_When_StartAsync_Then_ThePeerSeenLastIsDialedFirst()
    {
        // Arrange - NL-1357: 16 peers last seen a month ago that never answer (the startup dials run 16 at a time)
        // are listed before the one seen an hour ago; dialed in list order, it waited for a whole round of timeouts
        var peerManager = CreatePeerManager();
        peerManager.StartupDialWait = TimeSpan.FromMilliseconds(100);
        peerManager.ReconnectInitialDelay = TimeSpan.FromMinutes(1);
        var peers = Enumerable.Range(1, 16)
                              .Select(i => new PeerModel(new Key().PubKey.ToBytes(), "203.0.113.7", (uint)(9_000 + i),
                                                         "IPv4")
                              {
                                  LastSeenAt = DateTime.UtcNow - TimeSpan.FromDays(30),
                                  Channels = [CreateChannel(ChannelState.Open, (byte)i)]
                              })
                              .ToList();
        peers.Add(new PeerModel(new Key().PubKey.ToBytes(), "203.0.113.8", 9_735, "IPv4")
        {
            LastSeenAt = DateTime.UtcNow - TimeSpan.FromHours(1),
            Channels = [CreateChannel(ChannelState.Open, 17)]
        });
        _mockUnitOfWork.Setup(u => u.GetPeersForStartupAsync()).ReturnsAsync(peers);
        var dialed = new List<uint>();
        _mockTcpService.Setup(t => t.ConnectToPeerAsync(It.IsAny<PeerAddress>()))
                       .Returns(async (PeerAddress address) =>
                        {
                            lock (dialed)
                                dialed.Add((uint)address.Port);
                            await Task.Delay(TimeSpan.FromSeconds(1));
                            throw new ConnectionException("Timeout connecting to peer");
                        });

        // Act
        await peerManager.StartAsync(TestContext.Current.CancellationToken);
        List<uint> firstRound;
        lock (dialed)
            firstRound = [.. dialed];
        await peerManager.StopAsync();

        // Assert: the recent peer is in the first round of 16 dials (it was the 17th, behind every dead one)
        Assert.Equal(16, firstRound.Count);
        Assert.Contains(9_735u, firstRound);
    }

    [Fact]
    public async Task Given_ASlowStartupDial_When_StartAsyncReturned_Then_TheDialStillConnectsThePeer()
    {
        // Arrange - NL-576: the dial outlives the startup wait and is kept, not thrown away
        var peerManager = CreatePeerManager();
        peerManager.StartupDialWait = TimeSpan.FromMilliseconds(50);
        var peer = new PeerModel(_compactPubKey, ExpectedHost, ExpectedPort, ExpectedType)
        {
            Channels = [CreateChannel(ChannelState.Open, 1)]
        };
        _mockUnitOfWork.Setup(u => u.GetPeersForStartupAsync()).ReturnsAsync([peer]);
        _mockTcpService.Setup(t => t.ConnectToPeerAsync(It.IsAny<PeerAddress>()))
                       .Returns(async () =>
                        {
                            await Task.Delay(500);
                            return new ConnectedPeer(_compactPubKey, ExpectedHost, ExpectedPort,
                                                     new Mock<TcpClient>().Object);
                        });

        // Act
        await peerManager.StartAsync(TestContext.Current.CancellationToken);
        var connectedAtStart = peerManager.GetPeer(_compactPubKey) is not null;
        await WaitUntilAsync(() => peerManager.GetPeer(_compactPubKey) is not null);
        await peerManager.StopAsync();

        // Assert
        Assert.False(connectedAtStart);
        _mockTcpService.Verify(t => t.ConnectToPeerAsync(It.IsAny<PeerAddress>()), Times.Once);
    }

    [Fact]
    public async Task Given_TorOnAndAnAnnouncedOnion_When_APeerConnectsFromLoopback_Then_ItIsSavedAtItsOnionAndDialedBack()
    {
        // Arrange - NL-579: Tor delivers our onion service's connections from loopback; the peer announced an onion
        var announced = new GraphNode(_compactPubKey, 1, ReadOnlyMemory<byte>.Empty, new byte[GraphNode.AliasLength],
                                      new byte[GraphNode.ColorLength],
                                      [AddressDescriptor.FromHost(AddressDescriptorType.TorV3, PeerOnionHost, 9735)]);
        var graphStore = new Mock<IGraphStore>();
        graphStore.Setup(g => g.TryGetNode(_compactPubKey, out announced)).Returns(true);
        _fakeServiceProvider.AddService(typeof(IGraphStore), graphStore.Object);
        var peerManager = CreateTorPeerManager(TorMode.Hybrid);
        peerManager.ReconnectInitialDelay = TimeSpan.FromMilliseconds(10);
        peerManager.ReconnectMaxDelay = TimeSpan.FromMilliseconds(10);
        await peerManager.StartAsync(TestContext.Current.CancellationToken);
        PeerModel? saved = null;
        _mockPeerDbRepository.Setup(r => r.AddOrUpdateAsync(It.IsAny<PeerModel>()))
                             .Callback((PeerModel p) => saved = p)
                             .Returns(Task.CompletedTask);
        _mockChannelMemoryRepository.Setup(r => r.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
                                    .Returns([CreateChannel(ChannelState.Open, 1)]);
        var dialed = new List<PeerAddress>();
        _mockTcpService.Setup(t => t.ConnectToPeerAsync(It.IsAny<PeerAddress>()))
                       .Callback((PeerAddress a) =>
                        {
                            lock (dialed)
                                dialed.Add(a);
                        })
                       .ThrowsAsync(new ConnectionException("unreachable"));

        // Act
        RaiseInboundConnection("127.0.0.1");
        await WaitUntilAsync(() => saved is not null);
        RaiseDisconnect(_mockPeerService);
        await WaitUntilAsync(() =>
        {
            lock (dialed)
                return dialed.Count > 0;
        });
        await peerManager.StopAsync();

        // Assert: a dialable onion row, and the reconnect loop dials it
        Assert.False(saved!.IsInboundOnly);
        Assert.Equal(PeerOnionHost, saved.Host);
        Assert.Equal(9735U, saved.Port);
        Assert.Equal("TorV3", saved.Type);
        PeerAddress first;
        lock (dialed)
            first = dialed[0];
        Assert.Equal(PeerOnionHost, first.Host);
        Assert.Equal(9735, first.Port);
    }

    [Fact]
    public async Task Given_TorOffAndAnAnnouncedAddress_When_APeerConnectsFromLoopback_Then_ItStaysInboundOnly()
    {
        // Arrange - NL-497 unchanged without Tor: a loopback connection (a local tunnel) is not the announcement's
        var announced = new GraphNode(_compactPubKey, 1, ReadOnlyMemory<byte>.Empty, new byte[GraphNode.AliasLength],
                                      new byte[GraphNode.ColorLength],
                                      [AddressDescriptor.FromHost(AddressDescriptorType.IPv4, "203.0.113.7", 9736)]);
        var graphStore = new Mock<IGraphStore>();
        graphStore.Setup(g => g.TryGetNode(_compactPubKey, out announced)).Returns(true);
        _fakeServiceProvider.AddService(typeof(IGraphStore), graphStore.Object);
        var peerManager = CreateTorPeerManager(TorMode.Off);
        await peerManager.StartAsync(TestContext.Current.CancellationToken);
        PeerModel? saved = null;
        _mockPeerDbRepository.Setup(r => r.AddOrUpdateAsync(It.IsAny<PeerModel>()))
                             .Callback((PeerModel p) => saved = p)
                             .Returns(Task.CompletedTask);

        // Act
        RaiseInboundConnection("127.0.0.1");
        await WaitUntilAsync(() => saved is not null);
        await peerManager.StopAsync();

        // Assert
        Assert.True(saved!.IsInboundOnly);
        Assert.Equal(string.Empty, saved.Host);
    }

    private PeerManager CreateTorPeerManager(TorMode mode)
    {
        var options = Options.Create(new NodeOptions { Tor = new TorOptions { Mode = mode } });
        return new PeerManager(_mockChannelManager.Object, _mockChannelMemoryRepository.Object, _mockLogger.Object,
                               _mockPeerServiceFactory.Object, _mockSecureKeyManager.Object, _mockTcpService.Object,
                               _fakeServiceProvider, options);
    }
}