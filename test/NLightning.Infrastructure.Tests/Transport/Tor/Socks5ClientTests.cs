using System.Net;
using System.Net.Sockets;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Infrastructure.Tests.Transport.Tor;

using Infrastructure.Transport.Tor;

public sealed class Socks5ClientTests : IAsyncDisposable
{
    private const string Onion = "duckduckgogg42xjoc72x3sjasowoarfbgcmvfimaftt6twagswzczad.onion";

    private readonly TcpListener _echo = new(IPAddress.Loopback, 0);
    private readonly Task _echoLoop;

    public Socks5ClientTests()
    {
        _echo.Start();
        _echoLoop = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var client = await _echo.AcceptTcpClientAsync();
                    _ = Task.Run(async () =>
                    {
                        using (client)
                            await client.GetStream().CopyToAsync(client.GetStream());
                    });
                }
            }
            catch (Exception)
            {
                // Stopped
            }
        });
    }

    private IPEndPoint EchoEndPoint => (IPEndPoint)_echo.LocalEndpoint;

    [Fact]
    public async Task Given_AnOnionHost_When_Connecting_Then_TheNameGoesToTheProxyAndTheTunnelCarriesBytes()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var proxy = new FakeSocks5Proxy { Route = (_, _) => EchoEndPoint };
        using var client = await ConnectToProxyAsync(proxy);

        // Act
        await Socks5Client.ConnectAsync(client.GetStream(), Onion, 9735, null, ct);
        await client.GetStream().WriteAsync("ping"u8.ToArray(), ct);
        var echoed = new byte[4];
        await client.GetStream().ReadExactlyAsync(echoed, ct);

        // Assert
        var request = Assert.Single(proxy.Requests);
        Assert.Equal(Onion, request.Host);
        Assert.Equal(9735, request.Port);
        Assert.Equal(3, request.AddressType);
        Assert.Null(request.Username);
        Assert.Equal("ping"u8.ToArray(), echoed);
    }

    [Fact]
    public async Task Given_Credentials_When_Connecting_Then_TheyAreSentWithUsernamePasswordAuthentication()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var proxy = new FakeSocks5Proxy { RequireAuthentication = true, Route = (_, _) => EchoEndPoint };
        using var client = await ConnectToProxyAsync(proxy);

        // Act
        await Socks5Client.ConnectAsync(client.GetStream(), "203.0.113.7", 9735, ("user1", "pass1"), ct);

        // Assert
        var request = Assert.Single(proxy.Requests);
        Assert.Equal("203.0.113.7", request.Host);
        Assert.Equal(1, request.AddressType);
        Assert.Equal("user1", request.Username);
        Assert.Equal("pass1", request.Password);
    }

    [Fact]
    public async Task Given_AProxyThatWantsCredentials_When_ConnectingWithout_Then_ItFailsClearly()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var proxy = new FakeSocks5Proxy { RequireAuthentication = true };
        using var client = await ConnectToProxyAsync(proxy);

        // Act & Assert
        var e = await Assert.ThrowsAsync<Socks5Exception>(
            () => Socks5Client.ConnectAsync(client.GetStream(), Onion, 9735, null, ct));
        Assert.Contains("requires authentication", e.Message);
    }

    [Theory]
    [InlineData(0xF0, "descriptor not found")]
    [InlineData(0xF6, "invalid onion address")]
    [InlineData(0x04, "host unreachable")]
    [InlineData(0x06, "TTL expired")]
    public async Task Given_TorRefuses_When_Connecting_Then_TheReplyCodeIsExplained(byte code, string meaning)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var proxy = new FakeSocks5Proxy { ReplyCode = code };
        using var client = await ConnectToProxyAsync(proxy);

        // Act
        var e = await Assert.ThrowsAsync<Socks5Exception>(
            () => Socks5Client.ConnectAsync(client.GetStream(), Onion, 9735, null, ct));

        // Assert
        Assert.Equal(code, e.ReplyCode);
        Assert.Contains(meaning, e.Message);
        Assert.IsAssignableFrom<Domain.Exceptions.ConnectionException>(e);
    }

    [Fact]
    public async Task Given_ANonSocksServer_When_Connecting_Then_ItFailsClearly()
    {
        // Arrange: an HTTP-ish server that answers with other bytes
        var ct = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serve = Task.Run(async () =>
        {
            using var server = await listener.AcceptTcpClientAsync(ct);
            await server.GetStream().WriteAsync("HTTP/1.0 400\r\n"u8.ToArray(), ct);
            await Task.Delay(200, ct);
        }, ct);
        using var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, ct);

        // Act & Assert
        var e = await Assert.ThrowsAsync<Socks5Exception>(
            () => Socks5Client.ConnectAsync(client.GetStream(), Onion, 9735, null, ct));
        Assert.Contains("not 5", e.Message);
        await serve;
    }

    [Fact]
    public void Given_EachHostForm_When_TheRequestIsBuilt_Then_TheAddressTypeMatches()
    {
        // Act
        var ipv4 = Socks5Client.BuildConnectRequest("1.2.3.4", 9735);
        var ipv6 = Socks5Client.BuildConnectRequest("2001:db8::1", 9735);
        var name = Socks5Client.BuildConnectRequest("ab.onion", 80);

        // Assert
        Assert.Equal(Convert.FromHexString("05010001010203042607"), ipv4);
        Assert.Equal(Convert.FromHexString("0501000420010db80000000000000000000000012607"), ipv6);
        Assert.Equal(Convert.FromHexString("050100030861622e6f6e696f6e0050"), name);
    }

    [Fact]
    public void Given_AHostNameOver255Bytes_When_TheRequestIsBuilt_Then_ItIsRefused()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => Socks5Client.BuildConnectRequest(new string('a', 256), 80));
    }

    public async ValueTask DisposeAsync()
    {
        _echo.Stop();
        await _echoLoop;
    }

    private static async Task<TcpClient> ConnectToProxyAsync(FakeSocks5Proxy proxy)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPEndPoint.Parse(proxy.EndPoint));
        return client;
    }
}