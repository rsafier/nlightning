using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace NLightning.Application.Tests.Node.Managers;

using Domain.Crypto.ValueObjects;
using Domain.Node.Models;
using Domain.Node.ValueObjects;
using Infrastructure.Protocol.Models;
using Infrastructure.Transport.Interfaces;

/// <summary>
/// The host's peer transport seams (NL-1369) between two real peer managers with the real BOLT 8 stack: a node with
/// no listener of its own (<c>Node:ListenEnabled</c> false) is reached only through an in-test relay that hands each
/// connection to its <see cref="IPeerInboundAcceptor"/> as proxied, dialed through the other node's
/// <see cref="IPeerConnectionDialer"/>; the proxied peer is never saved at the relay's address; and every outbound
/// path (startup, the reconnect backoff, <c>ConnectToPeerAsync</c>, <c>DialPeerAsync</c>) dials through the dialer.
/// </summary>
public sealed partial class PeerManagerConnectTests
{
    [Fact]
    public async Task Given_ANodeWithoutAListener_When_APeerDialsItThroughARelay_Then_BothKeepTheConnection()
    {
        // Arrange: carol listens nowhere; the relay is her only way in
        var ct = TestContext.Current.CancellationToken;
        var carolPeers = new FakePeerDbRepository();
        var carol = await StartTransportNodeAsync(peerDbRepository: carolPeers, listenEnabled: false);
        await using var relay = new InTestRelay(carol.Acceptor);
        var dialer = new RecordingDialer(_ => relay.EndPoint);
        var bob = await StartTransportNodeAsync(dialer: dialer);
        var carolAddress = $"{Convert.ToHexString(carol.NodeId).ToLowerInvariant()}@203.0.113.50:9735";

        // Act
        var peer = await bob.PeerManager.ConnectToPeerAsync(new PeerAddressInfo(carolAddress));

        // Assert: the dial went through the dialer with carol's id, BOLT 8 and init ran over the relayed streams
        Assert.Equal(carol.NodeId, peer.NodeId);
        Assert.Equal("203.0.113.50", peer.Host);
        var (nodeId, address) = Assert.Single(dialer.Dials);
        Assert.Equal(carol.NodeId, nodeId);
        Assert.Equal("203.0.113.50", address.Host);
        Assert.Empty(carol.ListeningTo);
        await AssertStableConnectionAsync(bob, carol, "through the relay", ct);

        // The proxied peer is logged at the endpoint the relay gave, but saved inbound-only, never at that address
        Assert.Equal(InTestRelay.ProxiedClient.Address.ToString(), carol.PeerManager.GetPeer(bob.NodeId)!.Host);
        await WaitUntilAsync(() => carolPeers.Row(bob.NodeId) is not null, "carol saved bob's row", ct);
        var row = carolPeers.Row(bob.NodeId)!;
        Assert.True(row.IsInboundOnly);
        Assert.Equal((string.Empty, 0u), (row.Host, row.Port));
    }

    [Fact]
    public async Task Given_ADialableRowOfThePeer_When_ItConnectsThroughARelay_Then_TheRowKeepsItsAddress()
    {
        // Arrange: carol knows where bob listens; bob then reaches her through the relay
        var ct = TestContext.Current.CancellationToken;
        var carolPeers = new FakePeerDbRepository();
        var carol = await StartTransportNodeAsync(peerDbRepository: carolPeers, listenEnabled: false);
        await using var relay = new InTestRelay(carol.Acceptor);
        var bob = await StartTransportNodeAsync(dialer: new RecordingDialer(_ => relay.EndPoint));
        carolPeers.Update(new PeerModel(bob.NodeId, "192.0.2.10", 9735, "IPv4"));

        // Act
        await bob.PeerManager.ConnectToPeerAsync(
            new PeerAddressInfo($"{Convert.ToHexString(carol.NodeId).ToLowerInvariant()}@203.0.113.50:9735"));
        await AssertStableConnectionAsync(bob, carol, "through the relay", ct);
        await WaitUntilAsync(() => carolPeers.WriteCount(bob.NodeId) >= 2, "carol saved bob's row again", ct);

        // Assert: the saved address survives, not replaced by the relay's endpoint
        var row = carolPeers.Row(bob.NodeId)!;
        Assert.False(row.IsInboundOnly);
        Assert.Equal(("192.0.2.10", 9735u), (row.Host, row.Port));
    }

    [Fact]
    public async Task Given_ARegisteredDialer_When_EveryOutboundPathDials_Then_EachGoesThroughIt()
    {
        // Arrange: carol listens on loopback; bob's dialer reaches her whatever address it is given. Bob has carol
        // stored (the startup dial) and an active channel with her (the reconnect backoff)
        var ct = TestContext.Current.CancellationToken;
        var carol = await StartTransportNodeAsync();
        var dialer = new RecordingDialer(_ => new IPEndPoint(IPAddress.Loopback, carol.Port));
        string CarolAt(string host) => $"{Convert.ToHexString(carol.NodeId).ToLowerInvariant()}@{host}:9735";

        // Act 1: the startup dial
        var bob = await StartTransportNodeAsync(dialer: dialer,
                                                startupPeers: [new PeerModel(carol.NodeId, "203.0.113.60", 9735,
                                                                             "IPv4")],
                                                activeChannelWith: carol.NodeId);
        await AssertStableConnectionAsync(bob, carol, "startup dial", ct);

        // Act 2: carol drops bob, who reconnects with backoff
        carol.PeerManager.DisconnectPeer(bob.NodeId);
        await WaitUntilAsync(() => dialer.Dials.Count == 2, "the reconnect dialed", ct);
        await AssertStableConnectionAsync(bob, carol, "reconnect", ct);

        // Act 3: connect (the connect command, an open to a new peer, a restore, LND's ConnectPeer)
        await DisconnectAsync(bob, carol, ct);
        await bob.PeerManager.ConnectToPeerAsync(new PeerAddressInfo(CarolAt("203.0.113.61")));
        await AssertStableConnectionAsync(bob, carol, "connect", ct);

        // Act 4: a dial with a deadline (bootstrap and graph dials, LND's ConnectPeer with a timeout)
        await DisconnectAsync(bob, carol, ct);
        await bob.PeerManager.DialPeerAsync(new PeerAddressInfo(CarolAt("203.0.113.62")), ct);
        await AssertStableConnectionAsync(bob, carol, "dial", ct);

        // Assert: four dials, every one through the dialer with carol's id
        Assert.Equal(["203.0.113.60", "203.0.113.60", "203.0.113.61", "203.0.113.62"],
                     dialer.Dials.Select(d => d.Address.Host));
        Assert.All(dialer.Dials, d => Assert.Equal(carol.NodeId, d.NodeId));
    }

    private async Task<TestNode> StartTransportNodeAsync(FakePeerDbRepository? peerDbRepository = null,
                                                         IPeerConnectionDialer? dialer = null,
                                                         bool listenEnabled = true,
                                                         IReadOnlyList<PeerModel>? startupPeers = null,
                                                         CompactPubKey? activeChannelWith = null)
    {
        var node = await TestNode.StartAsync(_serializer, peerDbRepository, dialer: dialer,
                                             listenEnabled: listenEnabled, startupPeers: startupPeers,
                                             activeChannelWith: activeChannelWith);
        _nodes.Add(node);
        return node;
    }

    /// <summary>
    /// A tiny TCP relay, as a host in front of a node would run: it accepts connections on loopback and hands each
    /// one to the node's acceptor as a proxied stream from <see cref="ProxiedClient"/>.
    /// </summary>
    private sealed class InTestRelay : IAsyncDisposable
    {
        /// <summary>The endpoint the relay reports for every connection (where its client came from).</summary>
        public static readonly IPEndPoint ProxiedClient = new(IPAddress.Parse("198.51.100.20"), 41000);

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;

        public InTestRelay(IPeerInboundAcceptor target)
        {
            _listener.Start();
            _acceptLoop = AcceptLoopAsync(target);
        }

        public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndpoint;

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            try
            {
                await _acceptLoop;
            }
            catch (Exception)
            {
                // Stopped
            }

            _cts.Dispose();
        }

        private async Task AcceptLoopAsync(IPeerInboundAcceptor target)
        {
            while (!_cts.IsCancellationRequested)
            {
                var socket = await _listener.AcceptSocketAsync(_cts.Token);
                socket.NoDelay = true;
                await target.AcceptInboundAsync(new NetworkStream(socket, ownsSocket: true), ProxiedClient,
                                                isProxied: true, _cts.Token);
            }
        }
    }

    /// <summary>
    /// A host's dialer: records every dial and connects to the endpoint <paramref name="route"/> picks for it.
    /// </summary>
    private sealed class RecordingDialer(Func<PeerAddress, IPEndPoint> route) : IPeerConnectionDialer
    {
        private readonly ConcurrentQueue<(CompactPubKey NodeId, PeerAddress Address)> _dials = new();

        public IReadOnlyList<(CompactPubKey NodeId, PeerAddress Address)> Dials => [.. _dials];

        public async ValueTask<Stream?> DialAsync(CompactPubKey nodeId, PeerAddress address,
                                                  CancellationToken cancellationToken)
        {
            _dials.Enqueue((nodeId, address));
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(route(address), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }
}