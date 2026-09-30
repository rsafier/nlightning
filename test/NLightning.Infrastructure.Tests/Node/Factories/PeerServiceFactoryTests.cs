using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Tests.Node.Factories;

using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Gossip.Addresses;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Transport;
using Infrastructure.Node.Factories;

/// <summary>
/// NL-560: every path that gives up on a transport service disposes it, so its handshake state never waits for the
/// finalizer thread. NL-009: an inbound connection's remote endpoint becomes our init's <c>remote_addr</c>, unless it
/// is private or unusable.
/// </summary>
public class PeerServiceFactoryTests
{
    private static readonly CompactPubKey s_nodePubKey =
        Convert.FromHexString("028d7500dd4c12685d1f568b4c2b5048e8534b873319f3a8daa612b469132ec7f7");

    [Fact]
    public void Given_APublicIpv4EndPoint_When_TakenAsInboundRemoteAddress_Then_AnIpv4DescriptorIsBuilt()
    {
        // Act
        var descriptor = PeerServiceFactory.FromInboundEndPoint(new IPEndPoint(IPAddress.Parse("203.0.113.7"), 9735));

        // Assert
        Assert.Equal(AddressDescriptorType.IPv4, descriptor!.Type);
        Assert.Equal("203.0.113.7", descriptor.Host);
        Assert.Equal((ushort)9735, descriptor.Port);
    }

    [Theory]
    [InlineData("172.32.1.2", 9735)]
    [InlineData("192.0.2.1", 65535)]
    [InlineData("2606:4700:4700::1111", 9735)]
    public void Given_APublicOrRoutableButNotPrivateEndPoint_When_TakenAsInboundRemoteAddress_Then_ItIsSent(string host,
                                                                                                            int port)
    {
        // Act
        var descriptor = PeerServiceFactory.FromInboundEndPoint(new IPEndPoint(IPAddress.Parse(host), port));

        // Assert
        Assert.NotNull(descriptor);
    }

    [Fact]
    public void Given_AIpv4MappedPublicEndPoint_When_TakenAsInboundRemoteAddress_Then_TheMappedIpv4FormIsSent()
    {
        // Act
        var descriptor =
            PeerServiceFactory.FromInboundEndPoint(new IPEndPoint(IPAddress.Parse("::ffff:203.0.113.7"), 9735));

        // Assert
        Assert.Equal(AddressDescriptorType.IPv4, descriptor!.Type);
        Assert.Equal("203.0.113.7", descriptor.Host);
    }

    [Theory]
    [InlineData("127.0.0.1", 9735)]      // loopback
    [InlineData("10.1.2.3", 9735)]       // RFC 1918
    [InlineData("172.16.0.9", 9735)]     // RFC 1918 (172.16/12 runs to 172.31.255.255)
    [InlineData("172.31.255.1", 9735)]   // RFC 1918
    [InlineData("192.168.1.2", 9735)]    // RFC 1918
    [InlineData("169.254.9.9", 9735)]    // link-local
    [InlineData("0.0.0.0", 9735)]        // unspecified
    [InlineData("::1", 9735)]            // loopback
    [InlineData("::", 9735)]             // unspecified
    [InlineData("fd00::1", 9735)]        // unique-local
    [InlineData("fe80::1", 9735)]        // link-local
    [InlineData("ff02::1", 9735)]        // multicast
    [InlineData("::ffff:127.0.0.1", 9735)] // an IPv4-mapped loopback
    public void Given_APrivateEndPoint_When_TakenAsInboundRemoteAddress_Then_NothingIsSent(string host, int port)
    {
        // Act
        var descriptor = PeerServiceFactory.FromInboundEndPoint(new IPEndPoint(IPAddress.Parse(host), port));

        // Assert
        Assert.Null(descriptor);
    }

    [Fact]
    public void Given_ANonIpOrPortlessEndPoint_When_TakenAsInboundRemoteAddress_Then_NothingIsSent()
    {
        // Act / Assert
        Assert.Null(PeerServiceFactory.FromInboundEndPoint(new DnsEndPoint("peer.example.com", 9735)));
        Assert.Null(PeerServiceFactory.FromInboundEndPoint(new IPEndPoint(IPAddress.Parse("203.0.113.7"), 0)));
        Assert.Null(PeerServiceFactory.FromInboundEndPoint(null));
    }

    [Fact]
    public async Task Given_AHandshakeThatFails_When_APeerConnects_Then_TheTransportIsDisposed()
    {
        // Arrange
        var transport = new Mock<ITransportService>();
        transport.Setup(x => x.InitializeAsync()).ThrowsAsync(new InvalidOperationException("act one"));
        var factory = CreateFactory(transport.Object);
        using var connection = await LoopbackConnection.OpenAsync();

        // Act
        await Assert.ThrowsAsync<ConnectionException>(() => factory.CreateConnectingPeerAsync(connection.Accepted));

        // Assert
        transport.Verify(x => x.Dispose(), Times.Once);
    }

    [Fact]
    public async Task Given_AHandshakeWithoutARemoteKey_When_APeerConnects_Then_TheTransportIsDisposed()
    {
        // Arrange
        var transport = new Mock<ITransportService>();
        transport.Setup(x => x.InitializeAsync()).Returns(Task.CompletedTask);
        transport.SetupGet(x => x.RemoteStaticPublicKey).Returns((CompactPubKey?)null);
        var factory = CreateFactory(transport.Object);
        using var connection = await LoopbackConnection.OpenAsync();

        // Act
        await Assert.ThrowsAsync<ErrorException>(() => factory.CreateConnectingPeerAsync(connection.Accepted));

        // Assert
        transport.Verify(x => x.Dispose(), Times.Once);
    }

    [Fact]
    public async Task Given_AHandshakeThatFails_When_WeConnect_Then_TheTransportIsDisposed()
    {
        // Arrange
        var transport = new Mock<ITransportService>();
        transport.Setup(x => x.InitializeAsync()).ThrowsAsync(new InvalidOperationException("act two"));
        var factory = CreateFactory(transport.Object);
        using var connection = await LoopbackConnection.OpenAsync();

        // Act
        await Assert.ThrowsAsync<ConnectionException>(() => factory.CreateConnectedPeerAsync(s_nodePubKey,
                                                                                             connection.Client));

        // Assert
        transport.Verify(x => x.Dispose(), Times.Once);
    }

    private static PeerServiceFactory CreateFactory(ITransportService transport)
    {
        var keyManager = new Mock<ISecureKeyManager>();
        keyManager.Setup(x => x.GetNodePubKey()).Returns(s_nodePubKey);

        return new PeerServiceFactory(NullLoggerFactory.Instance, Mock.Of<IMessageFactory>(),
                                      Mock.Of<IMessageServiceFactory>(), keyManager.Object,
                                      new SingleTransportServiceFactory(transport),
                                      Options.Create(new NodeOptions()), Mock.Of<IServiceProvider>());
    }

    /// <summary>
    /// Hands out one transport service (Moq cannot set up the factory's span parameters).
    /// </summary>
    private sealed class SingleTransportServiceFactory(ITransportService transport) : ITransportServiceFactory
    {
        public ITransportService CreateTransportService(bool isInitiator, ReadOnlySpan<byte> s,
                                                        ReadOnlySpan<byte> rs, TcpClient tcpClient)
        {
            return transport;
        }

        public ITransportService CreateTransportService(bool isInitiator, ReadOnlySpan<byte> localStaticPublicKey,
                                                        ReadOnlySpan<byte> rs, TcpClient tcpClient,
                                                        ProtectedStaticEcdh protectedStaticEcdh)
        {
            return transport;
        }
    }

    /// <summary>
    /// A connected pair of TCP clients on loopback, so the factory can read the remote endpoint.
    /// </summary>
    private sealed class LoopbackConnection : IDisposable
    {
        private readonly TcpListener _listener;

        public TcpClient Client { get; }
        public TcpClient Accepted { get; }

        private LoopbackConnection(TcpListener listener, TcpClient client, TcpClient accepted)
        {
            _listener = listener;
            Client = client;
            Accepted = accepted;
        }

        public static async Task<LoopbackConnection> OpenAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient();
            var accept = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken).AsTask();
            await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, TestContext.Current.CancellationToken);

            return new LoopbackConnection(listener, client, await accept);
        }

        public void Dispose()
        {
            Client.Dispose();
            Accepted.Dispose();
            _listener.Stop();
        }
    }
}