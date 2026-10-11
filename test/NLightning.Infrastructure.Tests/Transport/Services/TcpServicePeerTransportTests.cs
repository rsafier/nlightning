using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Tests.Transport.Services;

using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Infrastructure.Protocol.Models;
using Infrastructure.Transport.Events;
using Infrastructure.Transport.Interfaces;
using Infrastructure.Transport.Services;

// ReSharper disable AccessToDisposedClosure
/// <summary>
/// The host's peer transport seams (NL-1369): an <see cref="IPeerConnectionDialer"/> is asked before every dial (its
/// stream becomes the connection; null leaves the dial to the node; a failure or a timeout is a
/// <see cref="ConnectionException"/>), and <see cref="IPeerInboundAcceptor.AcceptInboundAsync"/> raises a handed-over
/// stream like a connection of our own listener, only while the service listens. <c>Node:ListenEnabled=false</c>
/// opens no listener.
/// </summary>
public class TcpServicePeerTransportTests
{
    private static readonly CompactPubKey s_peerId =
        Convert.FromHexString("028d7500dd4c12685d1f568b4c2b5048e8534b873319f3a8daa612b469132ec7f7");

    [Fact]
    public async Task Given_ADialer_When_ConnectingToAPeer_Then_ItsStreamIsTheConnectionAndItSawTheNodeId()
    {
        // Arrange
        await using var stream = new MemoryStream();
        var dialer = new RecordingDialer(_ => stream);
        var service = CreateService(dialer: dialer);
        var address = new PeerAddress(s_peerId, "203.0.113.9", 9735);

        // Act
        var peer = await service.ConnectToPeerAsync(address);

        // Assert: the dialer got the node id and the address; the stream is the connection, marked proxied
        var (nodeId, dialed) = Assert.Single(dialer.Dials);
        Assert.Equal(s_peerId, nodeId);
        Assert.Equal(address, dialed);
        Assert.Null(peer.Connection.TcpClient);
        Assert.Same(stream, peer.Connection.GetStream());
        Assert.True(peer.Connection.IsProxied);
        Assert.Equal(("203.0.113.9", 9735u), (peer.Host, peer.Port));
        Assert.Equal(new DnsEndPoint("203.0.113.9", 9735), peer.Connection.RemoteEndPoint);
    }

    [Fact]
    public async Task Given_ADialerThatReturnsNull_When_ConnectingToAListeningPeer_Then_TheNodeDialsItself()
    {
        // Arrange
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var dialer = new RecordingDialer(_ => null);
        var service = CreateService(dialer: dialer);

        // Act
        var peer = await service.ConnectToPeerAsync(new PeerAddress(s_peerId, "127.0.0.1", port));
        using var accepted = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(dialer.Dials);
        Assert.NotNull(peer.Connection.TcpClient);
        Assert.True(peer.Connection.IsConnected);
        Assert.False(peer.Connection.IsProxied);
        peer.Connection.Dispose();
    }

    [Fact]
    public async Task Given_ADialerAndTorOff_When_ConnectingToAnOnionPeer_Then_TheDialerIsAskedFirst()
    {
        // Arrange: without the dialer an onion address fails at once (Tor off)
        const string onion = "duckduckgogg42xjoc72x3sjasowoarfbgcmvfimaftt6twagswzczad.onion";
        await using var stream = new MemoryStream();
        var dialer = new RecordingDialer(_ => stream);
        var service = CreateService(dialer: dialer);

        // Act
        var peer = await service.ConnectToPeerAsync(new PeerAddress(s_peerId, onion, 9735));

        // Assert
        Assert.Equal(onion, Assert.Single(dialer.Dials).Address.Host);
        Assert.Same(stream, peer.Connection.GetStream());
    }

    [Fact]
    public async Task Given_ADialerThatFails_When_Connecting_Then_AConnectionExceptionCarriesTheFailure()
    {
        // Arrange
        var failure = new IOException("proxy refused");
        var service = CreateService(dialer: new RecordingDialer(_ => throw failure));

        // Act
        var e = await Assert.ThrowsAsync<ConnectionException>(
                    () => service.ConnectToPeerAsync(new PeerAddress(s_peerId, "203.0.113.9", 9735)));

        // Assert
        Assert.Same(failure, e.InnerException);
    }

    [Fact]
    public async Task Given_ADialerThatHangs_When_Connecting_Then_NetworkTimeoutEndsTheDial()
    {
        // Arrange
        var dialer = new HangingDialer();
        var service = CreateService(dialer: dialer, networkTimeout: TimeSpan.FromMilliseconds(200));

        // Act
        var e = await Assert.ThrowsAsync<ConnectionException>(
                    () => service.ConnectToPeerAsync(new PeerAddress(s_peerId, "203.0.113.9", 9735)));

        // Assert: the dialer's token was cancelled
        Assert.Contains("Timeout", e.Message);
        Assert.True(dialer.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Given_ADialerThatReturnsAReadOnlyStream_When_Connecting_Then_TheDialFailsAndTheStreamIsClosed()
    {
        // Arrange
        var stream = new MemoryStream([1, 2, 3], writable: false);
        var service = CreateService(dialer: new RecordingDialer(_ => stream));

        // Act & Assert
        await Assert.ThrowsAsync<ConnectionException>(
            () => service.ConnectToPeerAsync(new PeerAddress(s_peerId, "203.0.113.9", 9735)));
        Assert.False(stream.CanRead);
    }

    [Fact]
    public async Task Given_AServiceThatIsNotListening_When_AStreamIsHandedOver_Then_ItIsRefused()
    {
        // Arrange
        var service = CreateService();
        await using var stream = new MemoryStream();

        // Act & Assert: before the start and after the stop the caller keeps its stream
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AcceptInboundAsync(stream, null, isProxied: true, TestContext.Current.CancellationToken));
        await service.StartListeningAsync(TestContext.Current.CancellationToken);
        await service.StopListeningAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AcceptInboundAsync(stream, null, isProxied: true, TestContext.Current.CancellationToken));
        Assert.True(stream.CanRead);
    }

    public static TheoryData<EndPoint?, string, uint> HandedOverEndPoints => new()
    {
        { new IPEndPoint(IPAddress.Parse("198.51.100.7"), 40123), "198.51.100.7", 40123 },
        { new DnsEndPoint("peer.example", 9735), "peer.example", 9735 },
        { null, "", 0 }
    };

    [Theory]
    [MemberData(nameof(HandedOverEndPoints))]
    public async Task Given_AListeningService_When_AStreamIsHandedOver_Then_TheEventCarriesItAndItsEndPoint(
        EndPoint? remoteEndPoint, string expectedHost, uint expectedPort)
    {
        // Arrange: no listener of its own
        var service = CreateService(listenEnabled: false);
        var raised = new List<NewPeerConnectedEventArgs>();
        service.OnNewPeerConnected += (_, e) => raised.Add(e);
        await service.StartListeningAsync(TestContext.Current.CancellationToken);
        await using var stream = new MemoryStream();

        try
        {
            // Act
            await service.AcceptInboundAsync(stream, remoteEndPoint, isProxied: true,
                                             TestContext.Current.CancellationToken);

            // Assert
            var args = Assert.Single(raised);
            Assert.Equal((expectedHost, expectedPort), (args.Host, args.Port));
            Assert.Null(args.Connection.TcpClient);
            Assert.True(args.Connection.IsProxied);
            Assert.Equal(remoteEndPoint, args.Connection.RemoteEndPoint);
            Assert.Same(stream, args.Connection.GetStream());
            Assert.Empty(service.ListeningTo);
        }
        finally
        {
            await service.StopListeningAsync();
        }
    }

    [Fact]
    public async Task Given_ListenEnabledFalse_When_TheServiceStarts_Then_NoListenerIsOpened()
    {
        // Arrange
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var service = CreateService(listenEnabled: false, listenAddresses: [$"127.0.0.1:{port}"]);

        // Act
        await service.StartListeningAsync(TestContext.Current.CancellationToken);

        try
        {
            // Assert: nothing bound the configured address
            Assert.Empty(service.ListeningTo);
            using var client = new TcpClient();
            await Assert.ThrowsAnyAsync<SocketException>(
                async () => await client.ConnectAsync(IPAddress.Loopback, port,
                                                      TestContext.Current.CancellationToken));
        }
        finally
        {
            await service.StopListeningAsync();
        }
    }

    [Fact]
    public async Task Given_InfrastructureServices_When_ADialerIsRegistered_Then_TheTcpServiceUsesItAndIsTheAcceptor()
    {
        // Arrange
        var dialer = new RecordingDialer(_ => new MemoryStream());
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(new NodeOptions()));
        services.AddInfrastructureServices();
        services.AddSingleton<IPeerConnectionDialer>(dialer);
        using var provider = services.BuildServiceProvider();

        // Act
        var tcpService = provider.GetRequiredService<ITcpService>();
        var acceptor = provider.GetRequiredService<IPeerInboundAcceptor>();
        await tcpService.ConnectToPeerAsync(new PeerAddress(s_peerId, "203.0.113.9", 9735));

        // Assert
        Assert.Same(tcpService, acceptor);
        Assert.Single(dialer.Dials);
    }

    private static TcpService CreateService(IPeerConnectionDialer? dialer = null, bool listenEnabled = true,
                                            List<string>? listenAddresses = null, TimeSpan? networkTimeout = null)
    {
        var options = new NodeOptions
        {
            ListenEnabled = listenEnabled,
            ListenAddresses = listenAddresses ?? ["127.0.0.1:0"],
            NetworkTimeout = networkTimeout ?? TimeSpan.FromSeconds(5)
        };
        return new TcpService(NullLogger<TcpService>.Instance, Options.Create(options), null, dialer);
    }

    private sealed class RecordingDialer(Func<PeerAddress, Stream?> dial) : IPeerConnectionDialer
    {
        private readonly List<(CompactPubKey NodeId, PeerAddress Address)> _dials = [];

        public IReadOnlyList<(CompactPubKey NodeId, PeerAddress Address)> Dials
        {
            get
            {
                lock (_dials)
                    return _dials.ToList();
            }
        }

        public ValueTask<Stream?> DialAsync(CompactPubKey nodeId, PeerAddress address,
                                            CancellationToken cancellationToken)
        {
            lock (_dials)
                _dials.Add((nodeId, address));

            return ValueTask.FromResult(dial(address));
        }
    }

    private sealed class HangingDialer : IPeerConnectionDialer
    {
        public CancellationToken Token { get; private set; }

        public async ValueTask<Stream?> DialAsync(CompactPubKey nodeId, PeerAddress address,
                                                  CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        }
    }
}