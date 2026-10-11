using System.Net.Sockets;
using NLightning.Infrastructure.Protocol.Models;

namespace NLightning.Application.Tests.Node.Managers;

using Application.Gossip.Graph.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Gossip.Addresses;
using Domain.Gossip.Graph;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.Options;
using Infrastructure.Node.ValueObjects;

/// <summary>
/// NL-1385: a peer we have channels with but only ever saw inbound (stored without a dialable address, NL-497) is
/// dialed at the addresses of its <c>node_announcement</c>, at startup and by the reconnect backoff.
/// </summary>
public partial class PeerManagerTests
{
    private const string AnnouncedIPv4 = "203.0.113.7";
    private const string AnnouncedIPv6 = "2001:db8::7";
    private const string AnnouncedDnsName = "node.example.com";

    private delegate bool TryGetNodeCallback(CompactPubKey nodeId, out GraphNode? node);

    public static TheoryData<TorMode, string[]> AnnouncedAddressOrders => new()
    {
        // Tor off: no onion; the IP addresses in announcement order, then the DNS name
        { TorMode.Off, [AnnouncedIPv4, $"[{AnnouncedIPv6}]", AnnouncedDnsName] },
        // Hybrid: the onion after the IP addresses
        { TorMode.Hybrid, [AnnouncedIPv4, $"[{AnnouncedIPv6}]", PeerOnionHost, AnnouncedDnsName] },
        // Tor-only: the onion first
        { TorMode.TorOnly, [PeerOnionHost, AnnouncedIPv4, $"[{AnnouncedIPv6}]", AnnouncedDnsName] }
    };

    [Theory]
    [MemberData(nameof(AnnouncedAddressOrders))]
    public async Task Given_AnInboundOnlyPeerWithAnAnnouncement_When_StartAsync_Then_ItsAnnouncedAddressesAreDialedInOrder(
        TorMode mode, string[] expectedHosts)
    {
        // Arrange: every address type, plus a loopback IP and localhost, which are never the peer's listening address
        AnnounceNode(AddressDescriptor.FromHost(AddressDescriptorType.Dns, AnnouncedDnsName, 9739),
                     AddressDescriptor.FromHost(AddressDescriptorType.IPv4, "127.0.0.1", 9735),
                     AddressDescriptor.FromHost(AddressDescriptorType.Dns, "localhost", 9735),
                     AddressDescriptor.FromHost(AddressDescriptorType.TorV3, PeerOnionHost, 9738),
                     AddressDescriptor.FromHost(AddressDescriptorType.IPv4, AnnouncedIPv4, 9736),
                     AddressDescriptor.FromHost(AddressDescriptorType.IPv6, AnnouncedIPv6, 9737));
        var peerManager = CreateTorPeerManager(mode);
        peerManager.ReconnectInitialDelay = TimeSpan.FromMinutes(1);
        SetupInboundOnlyStartupPeer();
        var dialed = RecordDials(_ => throw new ConnectionException("unreachable"));

        // Act
        await peerManager.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => Count(dialed) == expectedHosts.Length);
        await peerManager.StopAsync();

        // Assert: one attempt per announced address, in order, and nothing saved over the inbound-only row
        Assert.Equal(expectedHosts, Snapshot(dialed).Select(a => a.Host.Contains(':') ? $"[{a.Host}]" : a.Host));
        _mockPeerDbRepository.Verify(r => r.AddOrUpdateAsync(It.IsAny<PeerModel>()), Times.Never);
    }

    [Fact]
    public async Task Given_AnInboundOnlyPeerWithoutAnAnnouncement_When_StartAsyncAndBackoff_Then_ItIsNeverDialed()
    {
        // Arrange: a private peer (the graph knows nothing of it): we wait for it to connect, as before
        AnnounceNode();
        var peerManager = CreatePeerManager();
        peerManager.ReconnectInitialDelay = TimeSpan.FromMilliseconds(10);
        peerManager.ReconnectMaxDelay = TimeSpan.FromMilliseconds(10);
        SetupInboundOnlyStartupPeer();
        var dialed = RecordDials(_ => throw new ConnectionException("unreachable"));

        // Act
        await peerManager.StartAsync(TestContext.Current.CancellationToken);

        // Assert
        await AssertStaysAtAsync(() => Count(dialed), 0, "dials of a peer without an announcement");
        await peerManager.StopAsync();
    }

    [Fact]
    public async Task Given_AnInboundOnlyPeerThatAnswers_When_StartAsync_Then_ItIsConnectedAndSavedAtItsAnnouncedAddress()
    {
        // Arrange
        AnnounceNode(AddressDescriptor.FromHost(AddressDescriptorType.IPv4, AnnouncedIPv4, 9736));
        var peerManager = CreatePeerManager();
        SetupInboundOnlyStartupPeer();
        var saved = RecordSavedPeers();
        var dialed = RecordDials(ConnectedTo);

        // Act
        await peerManager.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => Count(saved) > 0);
        await peerManager.StopAsync();

        // Assert: the dial that connected proves the address, so the row becomes dialable there
        Assert.Equal(AnnouncedIPv4, Assert.Single(Snapshot(dialed)).Host);
        var row = Assert.Single(Snapshot(saved));
        Assert.False(row.IsInboundOnly);
        Assert.Equal(AnnouncedIPv4, row.Host);
        Assert.Equal(9736U, row.Port);
    }

    [Fact]
    public async Task Given_AnAnnouncementLearnedAfterTheStart_When_TheBackoffRuns_Then_ThePeerIsDialedThere()
    {
        // Arrange: the graph has no announcement of the peer at the start (a gossip sync brings it later)
        GraphNode? announcement = null;
        AnnounceNodeWith(() => announcement);
        var peerManager = CreatePeerManager();
        peerManager.ReconnectInitialDelay = TimeSpan.FromMilliseconds(10);
        peerManager.ReconnectMaxDelay = TimeSpan.FromMilliseconds(10);
        SetupInboundOnlyStartupPeer();
        var dialed = RecordDials(ConnectedTo);
        await peerManager.StartAsync(TestContext.Current.CancellationToken);
        await AssertStaysAtAsync(() => Count(dialed), 0, "dials before the announcement");

        // Act
        announcement = CreateAnnouncement(AddressDescriptor.FromHost(AddressDescriptorType.IPv4, AnnouncedIPv4, 9736));
        await WaitUntilAsync(() => peerManager.GetPeer(_compactPubKey) is not null);
        await peerManager.StopAsync();

        // Assert
        Assert.Equal(AnnouncedIPv4, Snapshot(dialed)[0].Host);
    }

    [Fact]
    public async Task Given_AStartupDialThatFails_When_TheBackoffRuns_Then_ItRetriesTheAnnouncedAddressUntilItConnects()
    {
        // Arrange
        AnnounceNode(AddressDescriptor.FromHost(AddressDescriptorType.IPv4, AnnouncedIPv4, 9736));
        var peerManager = CreatePeerManager();
        peerManager.ReconnectInitialDelay = TimeSpan.FromMilliseconds(10);
        peerManager.ReconnectMaxDelay = TimeSpan.FromMilliseconds(10);
        SetupInboundOnlyStartupPeer();
        var saved = RecordSavedPeers();
        var attempts = 0;
        var dialed = RecordDials(address => Interlocked.Increment(ref attempts) < 3
                                                ? throw new ConnectionException("unreachable")
                                                : ConnectedTo(address));

        // Act
        await peerManager.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => peerManager.GetPeer(_compactPubKey) is not null && Count(saved) > 0);
        await peerManager.StopAsync();

        // Assert: two failures saved nothing; the connection saved the announced address
        Assert.Equal(3, Count(dialed));
        Assert.All(Snapshot(dialed), a => Assert.Equal(AnnouncedIPv4, a.Host));
        var row = Assert.Single(Snapshot(saved));
        Assert.False(row.IsInboundOnly);
        Assert.Equal(AnnouncedIPv4, row.Host);
    }

    [Fact]
    public async Task Given_AnInboundOnlyPeerThatDrops_When_ItAnnouncedAnAddress_Then_TheBackoffDialsTheAnnouncedAddress()
    {
        // Arrange: Tor off, the peer connects from loopback and stays inbound-only (NL-497)
        AnnounceNode(AddressDescriptor.FromHost(AddressDescriptorType.IPv4, AnnouncedIPv4, 9736));
        var peerManager = CreatePeerManager();
        peerManager.ReconnectInitialDelay = TimeSpan.FromMilliseconds(10);
        peerManager.ReconnectMaxDelay = TimeSpan.FromMilliseconds(10);
        await peerManager.StartAsync(TestContext.Current.CancellationToken);
        var saved = RecordSavedPeers();
        _mockChannelMemoryRepository.Setup(r => r.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
                                    .Returns([CreateChannel(ChannelState.Open, 1)]);
        var dialed = RecordDials(_ => throw new ConnectionException("unreachable"));
        RaiseInboundConnection(ExpectedHost);
        await WaitUntilAsync(() => Count(saved) > 0);

        // Act
        RaiseDisconnect(_mockPeerService);
        await WaitUntilAsync(() => Count(dialed) >= 2);
        await peerManager.StopAsync();

        // Assert: never the loopback host it connected from, and the row stays inbound-only
        Assert.All(Snapshot(dialed), a => Assert.Equal(AnnouncedIPv4, a.Host));
        Assert.All(Snapshot(saved), p => Assert.True(p.IsInboundOnly));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_OurAnnouncedDialRacesThePeersConnection_When_BothComplete_Then_TheTieBreakKeepsOne(
        bool ourKeyIsLower)
    {
        // Arrange: our startup dial of the announced address is held while the peer connects to us from loopback
        _mockSecureKeyManager.Setup(k => k.GetNodePubKey()).Returns(ourKeyIsLower ? s_lowerNodeKey : s_higherNodeKey);
        AnnounceNode(AddressDescriptor.FromHost(AddressDescriptorType.IPv4, AnnouncedIPv4, 9736));
        var peerManager = CreatePeerManager();
        peerManager.StartupDialWait = TimeSpan.FromMilliseconds(10);
        peerManager.ReconnectInitialDelay = TimeSpan.FromMinutes(1);
        peerManager.SimultaneousConnectWindow = TimeSpan.FromMinutes(1);
        SetupInboundOnlyStartupPeer();
        var saved = RecordSavedPeers();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockTcpService.Setup(t => t.ConnectToPeerAsync(It.IsAny<PeerAddress>()))
                       .Returns(async (PeerAddress address) =>
                        {
                            entered.TrySetResult();
                            await release.Task;
                            return ConnectedTo(address);
                        });
        var inboundPeerService = CreateMockPeerService();
        SetupInboundPeerService(inboundPeerService);
        await peerManager.StartAsync(TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        RaiseInboundConnection(ExpectedHost);
        await WaitUntilAsync(() => peerManager.GetPeer(_compactPubKey) is not null);

        // Act
        release.SetResult();
        var loser = ourKeyIsLower ? inboundPeerService : _mockPeerService;
        await WaitUntilAsync(() => loser.Invocations.Any(i => i.Method.Name == nameof(IPeerService.Disconnect)));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Assert: the connection initiated by the lower key is kept (LND's rule, the peer keeps the same one)
        Assert.True(peerManager.GetPeer(_compactPubKey)!.TryGetPeerService(out var kept));
        Assert.Same(ourKeyIsLower ? _mockPeerService.Object : inboundPeerService.Object, kept);
        // Our dial saved the announced address only when its connection was kept
        Assert.Equal(ourKeyIsLower, Snapshot(saved).Any(p => p is { IsInboundOnly: false, Host: AnnouncedIPv4 }));
        await peerManager.StopAsync();
    }

    private void SetupInboundOnlyStartupPeer()
    {
        var peer = new PeerModel(_compactPubKey, string.Empty, 0, ExpectedType)
        {
            IsInboundOnly = true,
            Channels = [CreateChannel(ChannelState.Open, 1)]
        };
        _mockUnitOfWork.Setup(u => u.GetPeersForStartupAsync()).ReturnsAsync([peer]);
    }

    private GraphNode CreateAnnouncement(params AddressDescriptor[] addresses) =>
        new(_compactPubKey, 1, ReadOnlyMemory<byte>.Empty, new byte[GraphNode.AliasLength],
            new byte[GraphNode.ColorLength], addresses);

    /// <summary>Registers a graph that holds the peer's announcement with <paramref name="addresses"/> (none: no
    /// announcement of it).</summary>
    private void AnnounceNode(params AddressDescriptor[] addresses)
    {
        var announcement = addresses.Length > 0 ? CreateAnnouncement(addresses) : null;
        AnnounceNodeWith(() => announcement);
    }

    private void AnnounceNodeWith(Func<GraphNode?> announcement)
    {
        var graphStore = new Mock<IGraphStore>();
        graphStore.Setup(g => g.TryGetNode(It.IsAny<CompactPubKey>(), out It.Ref<GraphNode?>.IsAny))
                  .Returns(new TryGetNodeCallback((CompactPubKey nodeId, out GraphNode? node) =>
                   {
                       node = nodeId == _compactPubKey ? announcement() : null;
                       return node is not null;
                   }));
        _fakeServiceProvider.AddService(typeof(IGraphStore), graphStore.Object);
    }

    private List<PeerAddress> RecordDials(Func<PeerAddress, ConnectedPeer> dial)
    {
        var dialed = new List<PeerAddress>();
        _mockTcpService.Setup(t => t.ConnectToPeerAsync(It.IsAny<PeerAddress>()))
                       .Returns((PeerAddress address) =>
                        {
                            lock (dialed)
                                dialed.Add(address);
                            return Task.FromResult(dial(address));
                        });
        return dialed;
    }

    private List<PeerModel> RecordSavedPeers()
    {
        var saved = new List<PeerModel>();
        _mockPeerDbRepository.Setup(r => r.AddOrUpdateAsync(It.IsAny<PeerModel>()))
                             .Callback((PeerModel p) =>
                              {
                                  lock (saved)
                                      saved.Add(p);
                              })
                             .Returns(Task.CompletedTask);
        return saved;
    }

    private ConnectedPeer ConnectedTo(PeerAddress address) =>
        new(_compactPubKey, address.Host, (uint)address.Port, new Mock<TcpClient>().Object);

    private static int Count<T>(List<T> list)
    {
        lock (list)
            return list.Count;
    }

    private static List<T> Snapshot<T>(List<T> list)
    {
        lock (list)
            return [.. list];
    }
}