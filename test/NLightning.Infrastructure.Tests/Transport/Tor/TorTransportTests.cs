using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Infrastructure.Tests.Transport.Tor;

using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Infrastructure.Protocol.Models;
using Infrastructure.Transport.Services;
using Infrastructure.Transport.Tor;

/// <summary>
/// The routes <see cref="TcpService"/> takes for each <c>Node:Tor</c> mode, the SOCKS5 dialer's isolation and the
/// HTTP handler of Tor-only mode, against an in-process SOCKS5 proxy.
/// </summary>
public sealed class TorTransportTests : IAsyncDisposable
{
    private const string Onion = "duckduckgogg42xjoc72x3sjasowoarfbgcmvfimaftt6twagswzczad.onion";

    private static readonly CompactPubKey s_pubKey =
        Convert.FromHexString("028d7500dd4c12685d1f568b4c2b5048e8534b873319f3a8daa612b469132ec7f7");

    private readonly TcpListener _target = new(IPAddress.Loopback, 0);
    private readonly FakeSocks5Proxy _proxy;

    public TorTransportTests()
    {
        _target.Start();
        _proxy = new FakeSocks5Proxy { RequireAuthentication = true, Route = (_, _) => TargetEndPoint };
    }

    private IPEndPoint TargetEndPoint => (IPEndPoint)_target.LocalEndpoint;

    [Fact]
    public async Task Given_TorOff_When_DialingAnOnion_Then_TheErrorSaysToTurnTorOn()
    {
        // Arrange
        var service = CreateTcpService(TorMode.Off);

        // Act & Assert
        var e = await Assert.ThrowsAsync<ConnectionException>(
            () => service.ConnectToPeerAsync(new PeerAddress(s_pubKey, Onion, 9735)));
        Assert.Contains("Node:Tor:Mode", e.Message);
        Assert.Empty(_proxy.Requests);
    }

    [Fact]
    public async Task Given_Hybrid_When_DialingAnOnion_Then_ItGoesThroughTorWithIsolatedCredentials()
    {
        // Arrange
        var service = CreateTcpService(TorMode.Hybrid);

        // Act
        var first = await service.ConnectToPeerAsync(new PeerAddress(s_pubKey, Onion, 9735));
        var second = await service.ConnectToPeerAsync(new PeerAddress(s_pubKey, Onion, 9735));

        // Assert
        Assert.Equal(Onion, first.Host);
        Assert.Equal(2, _proxy.Requests.Count);
        Assert.All(_proxy.Requests, r => Assert.Equal((Onion, 9735, (byte)3), (r.Host, r.Port, r.AddressType)));
        var usernames = _proxy.Requests.Select(r => r.Username).ToList();
        Assert.All(usernames, u => Assert.StartsWith("nltg-", u));
        Assert.NotEqual(usernames[0], usernames[1]);
        first.TcpClient.Dispose();
        second.TcpClient.Dispose();
    }

    [Fact]
    public async Task Given_Hybrid_When_DialingAnIpAddress_Then_ItIsDirect()
    {
        // Arrange
        var service = CreateTcpService(TorMode.Hybrid);

        // Act
        var peer = await service.ConnectToPeerAsync(new PeerAddress(s_pubKey, "127.0.0.1", TargetEndPoint.Port));

        // Assert
        Assert.Empty(_proxy.Requests);
        Assert.True(peer.TcpClient.Connected);
        peer.TcpClient.Dispose();
    }

    [Theory]
    [InlineData("203.0.113.7", 1)]
    [InlineData("node.invalid", 3)]
    public async Task Given_TorOnly_When_DialingAClearnetPeer_Then_ItGoesThroughTorUnresolved(string host,
                                                                                              byte addressType)
    {
        // Arrange
        var service = CreateTcpService(TorMode.TorOnly);

        // Act
        var peer = await service.ConnectToPeerAsync(new PeerAddress(s_pubKey, host, 9735));

        // Assert: a host name reaches Tor as a name (resolving node.invalid locally would have failed)
        var request = Assert.Single(_proxy.Requests);
        Assert.Equal(host, request.Host);
        Assert.Equal(addressType, request.AddressType);
        Assert.IsType<TorTcpClient>(peer.TcpClient);
        peer.TcpClient.Dispose();
    }

    [Fact]
    public async Task Given_TorOnly_When_DialingALoopbackPeer_Then_ItIsDirect()
    {
        // Arrange - NL-588: Tor refuses loopback and private targets, and such a connection never leaves the host/LAN
        var service = CreateTcpService(TorMode.TorOnly);

        // Act
        var peer = await service.ConnectToPeerAsync(new PeerAddress(s_pubKey, "127.0.0.1", TargetEndPoint.Port));

        // Assert
        Assert.Empty(_proxy.Requests);
        Assert.True(peer.TcpClient.Connected);
        Assert.IsNotType<TorTcpClient>(peer.TcpClient);
        peer.TcpClient.Dispose();
    }

    [Theory]
    [InlineData(TorMode.Off, false, false, 5)]
    [InlineData(TorMode.Hybrid, true, false, 30)]
    [InlineData(TorMode.Hybrid, false, false, 5)]
    [InlineData(TorMode.TorOnly, false, true, 30)]
    [InlineData(TorMode.TorOnly, false, false, 5)]
    public async Task Given_AConnection_When_ItsNetworkTimeoutIsChosen_Then_ATorRoutedOneWaitsLonger(
        TorMode mode, bool dialedThroughTor, bool inboundFromLoopback, int expectedSeconds)
    {
        // Arrange - NL-590: NetworkTimeout 5 s, Tor ConnectTimeout 60 s, so a connection over Tor waits 30 s
        var ct = TestContext.Current.CancellationToken;
        var options = CreateOptions(mode, _proxy.EndPoint, TimeSpan.FromSeconds(60));
        options.Tor.OnionServiceEnabled = mode != TorMode.Off;
        using var client = dialedThroughTor ? new TorTcpClient() : new TcpClient();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, ct);
        using var accepted = await listener.AcceptTcpClientAsync(ct);

        // Act
        var timeout = TorTcpClient.GetNetworkTimeout(options, inboundFromLoopback ? accepted : client,
                                                     inbound: inboundFromLoopback);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), timeout);
    }

    [Fact]
    public async Task Given_TorRefusesAnOnion_When_Dialing_Then_TheConnectionExceptionCarriesTorsReason()
    {
        // Arrange
        await using var refusing = new FakeSocks5Proxy { ReplyCode = 0xF0 };
        var service = CreateTcpService(TorMode.Hybrid, refusing.EndPoint);

        // Act & Assert
        var e = await Assert.ThrowsAsync<Socks5Exception>(
            () => service.ConnectToPeerAsync(new PeerAddress(s_pubKey, Onion, 9735)));
        Assert.Contains("descriptor not found", e.Message);
    }

    [Fact]
    public async Task Given_TorNotRunning_When_DialingAnOnion_Then_TheErrorNamesTheSocksPort()
    {
        // Arrange
        var service = CreateTcpService(TorMode.Hybrid, "127.0.0.1:1");

        // Act & Assert
        var e = await Assert.ThrowsAsync<ConnectionException>(
            () => service.ConnectToPeerAsync(new PeerAddress(s_pubKey, Onion, 9735)));
        Assert.Contains("SOCKS5 port 127.0.0.1:1", e.Message);
    }

    [Fact]
    public async Task Given_ASlowOnion_When_Dialing_Then_TorsConnectTimeoutApplies()
    {
        // Arrange: a "proxy" that accepts and never answers
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var service = CreateTcpService(TorMode.Hybrid, $"127.0.0.1:{((IPEndPoint)silent.LocalEndpoint).Port}",
                                       TimeSpan.FromMilliseconds(300));

        // Act & Assert
        var e = await Assert.ThrowsAsync<ConnectionException>(
            () => service.ConnectToPeerAsync(new PeerAddress(s_pubKey, Onion, 9735)));
        Assert.Contains("through Tor", e.Message);
    }

    [Fact]
    public async Task Given_TorOnly_When_TheNodeMakesAnHttpRequest_Then_ItGoesThroughTorByName()
    {
        // Arrange: the "web server" behind Tor answers one request
        var ct = TestContext.Current.CancellationToken;
        var serve = Task.Run(async () =>
        {
            using var client = await _target.AcceptTcpClientAsync(ct);
            var stream = client.GetStream();
            var buffer = new byte[4096];
            _ = await stream.ReadAsync(buffer, ct);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"), ct);
        }, ct);
        var provider = CreateProvider(TorMode.TorOnly, _proxy.EndPoint);
        using var http = new HttpClient(TorHttpHandler.Create(provider, TimeSpan.FromMinutes(1)));

        // Act
        var body = await http.GetStringAsync("http://fees.invalid:8080/api", ct);

        // Assert
        Assert.Equal("ok", body);
        var request = Assert.Single(_proxy.Requests);
        Assert.Equal(("fees.invalid", 8080), (request.Host, request.Port));
        await serve;
    }

    [Fact]
    public void Given_Hybrid_When_TheHttpHandlerIsMade_Then_ItConnectsDirectly()
    {
        // Arrange
        var provider = CreateProvider(TorMode.Hybrid, _proxy.EndPoint);

        // Act
        using var handler = TorHttpHandler.Create(provider, TimeSpan.FromMinutes(1));

        // Assert
        Assert.Null(handler.ConnectCallback);
    }

    [Fact]
    public void Given_TorOnlyWithoutADialer_When_TheHttpHandlerIsMade_Then_ItRefusesInsteadOfGoingClearnet()
    {
        // Arrange - NL-580
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(CreateOptions(TorMode.TorOnly, _proxy.EndPoint, null)));
        using var provider = services.BuildServiceProvider();

        // Act & Assert
        var e = Assert.Throws<InvalidOperationException>(() => TorHttpHandler.Create(provider, TimeSpan.FromMinutes(1)));
        Assert.Contains("ITorSocksDialer", e.Message);
    }

    public async ValueTask DisposeAsync()
    {
        _target.Stop();
        await _proxy.DisposeAsync();
    }

    private TcpService CreateTcpService(TorMode mode, string? socks = null, TimeSpan? connectTimeout = null)
    {
        var options = Options.Create(CreateOptions(mode, socks ?? _proxy.EndPoint, connectTimeout));
        return new TcpService(NullLogger<TcpService>.Instance, options,
                              new TorSocksDialer(NullLogger<TorSocksDialer>.Instance, options));
    }

    private static ServiceProvider CreateProvider(TorMode mode, string socks)
    {
        var options = Options.Create(CreateOptions(mode, socks, null));
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton<ITorSocksDialer>(new TorSocksDialer(NullLogger<TorSocksDialer>.Instance, options));
        return services.BuildServiceProvider();
    }

    private static NodeOptions CreateOptions(TorMode mode, string socks, TimeSpan? connectTimeout) => new()
    {
        NetworkTimeout = TimeSpan.FromSeconds(5),
        Tor = new TorOptions
        {
            Mode = mode,
            SocksProxy = socks,
            OnionServiceEnabled = false,
            ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(10)
        }
    };
}