using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Tests.Transport.Services;

using Domain.Exceptions;
using Domain.Node.Options;
using Infrastructure.Protocol.Models;
using Infrastructure.Transport.Events;
using Infrastructure.Transport.Services;
using NLightning.Tests.Utils;

// ReSharper disable AccessToDisposedClosure
/// <summary>
/// NL-178: <c>TcpService</c> over real loopback sockets: the listener accepts a connection and raises its event, a
/// connect to a listener works end to end (bytes both ways), a refused connect becomes a <c>ConnectionException</c>,
/// a bad listen address is skipped, and stop closes the listeners. NL-806: both directions have TCP keepalive on. NL-107: IPv6 listen addresses
/// (<c>[ipv6]:port</c>, a bare address on the default port) parse and bind, the wildcard <c>[::]</c> dual-stack. Both
/// directions have Nagle off (found by the cluster harness: our <c>stfu</c> reached CLN 40 ms after the
/// <c>revoke_and_ack</c> before it, behind the peer's delayed ACK, and crossed CLN's fulfill, NL-477).
/// </summary>
public class TcpServiceTests
{
    /// <summary>
    /// NL-806: TCP keepalive on both directions' peer sockets, a second guard behind the ping keep-alive.
    /// </summary>
    private static void AssertKeepAlive(Socket socket)
    {
        Assert.NotEqual(0, (int)socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)!);
        Assert.Equal(TcpService.KeepAliveTimeSeconds,
                     (int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime)!);
        Assert.Equal(TcpService.KeepAliveIntervalSeconds,
                     (int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval)!);
        Assert.Equal(TcpService.KeepAliveRetryCount,
                     (int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount)!);
    }

    [Fact]
    public async Task Given_AListener_When_AClientConnects_Then_TheEventCarriesThePeerAndAConnectedClient()
    {
        // Arrange
        var port = await PortPoolUtil.GetAvailablePortAsync();
        var service = CreateService([$"127.0.0.1:{port}"]);
        var connected = new TaskCompletionSource<NewPeerConnectedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.OnNewPeerConnected += (_, e) => connected.TrySetResult(e);

        try
        {
            await service.StartListeningAsync(TestContext.Current.CancellationToken);
            using var client = new TcpClient();

            // Act
            await client.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
            var eventArgs = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5),
                                                          TestContext.Current.CancellationToken);

            // Assert: the event names the peer's end (its ephemeral port), not our listener
            Assert.Equal("127.0.0.1", eventArgs.Host);
            Assert.NotEqual((uint)port, eventArgs.Port);
            Assert.True(eventArgs.TcpClient.Connected);
            Assert.True(eventArgs.TcpClient.NoDelay); // Nagle off: no delayed-ACK stall between two peer messages
            AssertKeepAlive(eventArgs.TcpClient.Client);
            Assert.Single(service.ListeningTo);
        }
        finally
        {
            await service.StopListeningAsync();
        }
    }

    [Fact]
    public async Task Given_APeerListener_When_Connecting_Then_TheConnectedPeerSendsAndReceives()
    {
        // Arrange: a raw listener as the peer
        var port = await PortPoolUtil.GetAvailablePortAsync();
        using var peerListener = new TcpListener(IPAddress.Loopback, port);
        peerListener.Start();
        var peerAccepted = Task.Run(async () =>
        {
            var peer = await peerListener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            var stream = peer.GetStream();
            var buffer = new byte[5];
            await stream.ReadExactlyAsync(buffer, TestContext.Current.CancellationToken);
            await stream.WriteAsync(new byte[] { 0x0a, 0x0b, 0x0c }, TestContext.Current.CancellationToken);
            return peer;
        }, TestContext.Current.CancellationToken);
        var service = CreateService([]);
        PeerAddress peerAddress = new($"02{new string('0', 64)}@127.0.0.1:{port}");

        try
        {
            // Act
            var connectedPeer = await service.ConnectToPeerAsync(peerAddress);

            // Assert: the service handed back a live client; bytes travel both ways
            Assert.Equal(peerAddress.Host.ToString(), connectedPeer.Host);
            Assert.Equal((uint)port, connectedPeer.Port);
            Assert.Equal(peerAddress.PubKey, connectedPeer.CompactPubKey);
            Assert.True(connectedPeer.TcpClient.Connected);
            Assert.True(connectedPeer.TcpClient.NoDelay);
            AssertKeepAlive(connectedPeer.TcpClient.Client);
            await connectedPeer.TcpClient.Client.SendAsync(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 },
                                                           TestContext.Current.CancellationToken);
            var peer = await peerAccepted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var reply = new byte[3];
            await connectedPeer.TcpClient.GetStream()
                         .ReadExactlyAsync(reply, TestContext.Current.CancellationToken);
            Assert.Equal(new byte[] { 0x0a, 0x0b, 0x0c }, reply);
            connectedPeer.TcpClient.Dispose();
            peer.Dispose();
        }
        finally
        {
            peerListener.Stop();
        }
    }

    [Fact]
    public async Task Given_AClosedPort_When_Connecting_Then_AConnectionExceptionIsThrown()
    {
        // Arrange: take a port and release it, so nothing listens there
        var port = await PortPoolUtil.GetAvailablePortAsync();
        var holder = new TcpListener(IPAddress.Loopback, port);
        holder.Start();
        holder.Stop();
        var service = CreateService([]);
        PeerAddress peerAddress = new($"02{new string('0', 64)}@127.0.0.1:{port}");

        // Act
        var exception = await Assert.ThrowsAsync<ConnectionException>(
            () => service.ConnectToPeerAsync(peerAddress));

        // Assert
        Assert.Contains($"Failed to connect to peer 127.0.0.1:{port}", exception.Message);
    }

    [Fact]
    public async Task Given_AnInvalidListenAddress_When_TheServiceStarts_Then_ItIsSkippedAndNothingIsListening()
    {
        // Arrange: a host name is not a listen address, ":::9735" is no IPv6 endpoint and port 0 gives nobody a port
        var service = CreateService(["localhost:9735", ":::9735", "[::1]:0"]);

        try
        {
            // Act
            await service.StartListeningAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Empty(service.ListeningTo);
        }
        finally
        {
            await service.StopListeningAsync();
        }
    }

    [Theory]
    [InlineData("[::]:9735", "::", 9735)]
    [InlineData("[2001:db8::1]:9735", "2001:db8::1", 9735)]
    [InlineData("[::1]:19735", "::1", 19735)]
    [InlineData("::", "::", 9735)]
    [InlineData(" ::1 ", "::1", 9735)]
    [InlineData("0.0.0.0", "0.0.0.0", 9735)]
    [InlineData("2001:db8::1", "2001:db8::1", 9735)]
    [InlineData("127.0.0.1:9735", "127.0.0.1", 9735)]
    public void Given_AValidListenAddress_When_Parsed_Then_TheEndPointIsRead(string config, string expectedAddress,
                                                                            int expectedPort)
    {
        // Act
        var parsed = TcpService.TryParseListenAddress(config, out var endPoint);

        // Assert: a bare address takes the BOLT 7 default port (NL-107)
        Assert.True(parsed);
        Assert.Equal(expectedAddress, endPoint.Address.ToString());
        Assert.Equal(expectedPort, endPoint.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData(":9735")]
    [InlineData("[::1]")]
    [InlineData("[::]:0")]
    [InlineData("[::1]:99999")]
    [InlineData(":::9735")]
    [InlineData("localhost:9735")]
    [InlineData("2001:db8::1:9735:extra")]
    public void Given_AnInvalidListenAddress_When_Parsed_Then_ItIsRefused(string? config)
    {
        Assert.False(TcpService.TryParseListenAddress(config, out var endPoint));
        Assert.Equal(IPAddress.None, endPoint.Address);
    }

    [Fact]
    public async Task Given_AnIPv6LoopbackListenAddress_When_AClientConnectsOverV6_Then_TheEventCarriesThePeer()
    {
        Assert.SkipUnless(s_ipv6LoopbackAvailable.Value, "The host has no IPv6 loopback (NL-615)");

        // Arrange
        var port = await PortPoolUtil.GetAvailablePortAsync();
        var service = CreateService([$"[::1]:{port}"]);
        var connected = new TaskCompletionSource<NewPeerConnectedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.OnNewPeerConnected += (_, e) => connected.TrySetResult(e);

        try
        {
            await service.StartListeningAsync(TestContext.Current.CancellationToken);
            Assert.Equal($"[::1]:{port}", service.ListeningTo.Single().ToString());
            using var client = new TcpClient();

            // Act
            await client.ConnectAsync(IPAddress.IPv6Loopback, port, TestContext.Current.CancellationToken);
            var eventArgs = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5),
                                                          TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal("::1", eventArgs.Host);
            Assert.True(eventArgs.TcpClient.Connected);
        }
        finally
        {
            await service.StopListeningAsync();
        }
    }

    [Fact]
    public async Task Given_AWildcardV6ListenAddress_When_ClientsConnectOverBothFamilies_Then_BothAreAccepted()
    {
        Assert.SkipUnless(s_ipv6LoopbackAvailable.Value, "The host has no IPv6 loopback (NL-615)");

        // Arrange: [::] is bound dual-stack, so an IPv4 peer (loopback here) reaches it as a mapped address too
        var port = await PortPoolUtil.GetAvailablePortAsync();
        var service = CreateService([$"[::]:{port}"]);
        var events = new List<NewPeerConnectedEventArgs>();
        var bothReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.OnNewPeerConnected += (_, e) =>
        {
            lock (events)
            {
                events.Add(e);
                if (events.Count == 2)
                    bothReceived.TrySetResult();
            }
        };

        try
        {
            await service.StartListeningAsync(TestContext.Current.CancellationToken);
            using var clientV6 = new TcpClient(AddressFamily.InterNetworkV6);
            using var clientV4 = new TcpClient(AddressFamily.InterNetwork);

            // Act
            await clientV6.ConnectAsync(IPAddress.IPv6Loopback, port, TestContext.Current.CancellationToken);
            await clientV4.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
            await bothReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            // Assert: one peer per family, the IPv4 one through its mapped form
            Assert.Equal(2, events.Count);
            Assert.Contains("::1", events.Select(e => e.Host));
            Assert.Contains(events.Select(e => e.Host), h => h.EndsWith("127.0.0.1", StringComparison.Ordinal));
            Assert.All(events, e => Assert.True(e.TcpClient.Connected));
        }
        finally
        {
            await service.StopListeningAsync();
        }
    }

    [Fact]
    public async Task Given_ARunningListener_When_TheServiceStops_Then_FurtherConnectionsAreRefused()
    {
        // Arrange
        var port = await PortPoolUtil.GetAvailablePortAsync();
        var service = CreateService([$"127.0.0.1:{port}"]);
        await service.StartListeningAsync(TestContext.Current.CancellationToken);
        Assert.Single(service.ListeningTo);

        // Act
        await service.StopListeningAsync();

        // Assert: the port no longer accepts connections
        Assert.Empty(service.ListeningTo);
        using var client = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(
            () => client.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task Given_AServiceThatNeverStarted_When_Stopped_Then_AnInvalidOperationExceptionIsThrown()
    {
        // Arrange
        var service = CreateService([]);

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StopListeningAsync());
    }

    /// <summary>
    /// Whether this host can bind the IPv6 loopback (NL-615): a container without an IPv6 stack reports
    /// <see cref="Socket.OSSupportsIPv6"/> yet refuses <c>[::1]</c>.
    /// </summary>
    private static readonly Lazy<bool> s_ipv6LoopbackAvailable = new(() =>
    {
        if (!Socket.OSSupportsIPv6)
            return false;

        try
        {
            using var probe = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            probe.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    });

    private static TcpService CreateService(IReadOnlyList<string> listenAddresses)
    {
        return new TcpService(NullLogger<TcpService>.Instance,
                              Options.Create(new NodeOptions { ListenAddresses = [.. listenAddresses] }));
    }
}