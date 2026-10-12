using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Infrastructure.Tests.Protocol.Dns;

using Domain.Node.Options;
using Infrastructure.Protocol.Dns;
using Infrastructure.Transport.Tor;

public sealed class TorSocksDnsRecordLookupTests : IAsyncDisposable
{
    private readonly FakeSocks5Proxy _proxy;
    private Func<string, int, IPEndPoint?> _route = KeepRoute;
    private readonly NodeOptions _nodeOptions = new()
    {
        Tor = { Mode = TorMode.TorOnly, ConnectTimeout = TimeSpan.FromSeconds(5) },
        Bootstrap = { QueryTimeout = TimeSpan.FromSeconds(5) }
    };

    public TorSocksDnsRecordLookupTests()
    {
        _proxy = new FakeSocks5Proxy { Route = (host, port) => _route(host, port) };
    }

    [Fact]
    public async Task Given_AResolverBehindTheSocksPort_When_Queried_Then_TheSeedAnswerComesBack()
    {
        // Arrange: a DNS-over-TCP server that answers one SRV record with glue, behind the fake proxy
        var (dnsHost, dnsPort) = await StartDnsServerAsync(Response());
        _nodeOptions.Tor.SocksProxy = _proxy.EndPoint;
        _nodeOptions.Bootstrap.TorNameServer = $"{dnsHost}:{dnsPort}";
        _route = (host, port) => host == dnsHost && port == dnsPort
            ? new IPEndPoint(IPAddress.Loopback, dnsPort)
            : null;
        var lookup = CreateLookup();

        // Act
        var response = await lookup.QueryAsync("nodes.lightning.directory", DnsRecordKind.Srv,
                                               TestContext.Current.CancellationToken);

        // Assert
        Assert.True(lookup.IsAvailable);
        Assert.Equal(DnsLookupStatus.NoError, response.Status);
        var srv = Assert.Single(response.Srv);
        Assert.Equal((ushort)9735, srv.Port);
        Assert.Equal("ln1qz.target", srv.Target);
        Assert.Equal(IPAddress.Parse("9.9.9.9"), Assert.Single(response.Additional).Address);

        // The query went through the proxy, as a connection to the resolver
        var request = Assert.Single(_proxy.Requests);
        Assert.Equal(dnsHost, request.Host);
        Assert.Equal(dnsPort, request.Port);
    }

    [Fact]
    public async Task Given_TheProxyRefuses_When_Queried_Then_TheAnswerIsOther()
    {
        // Arrange: nothing routes, the proxy refuses like a Tor port without a path
        _nodeOptions.Tor.SocksProxy = _proxy.EndPoint;
        _nodeOptions.Bootstrap.TorNameServer = "127.0.0.1:53";
        var lookup = CreateLookup();

        // Act
        var response = await lookup.QueryAsync("nodes.lightning.directory", DnsRecordKind.Srv,
                                               TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(DnsLookupStatus.Other, response.Status);
    }

    [Fact]
    public async Task Given_AResolverThatSendsGarbage_When_Queried_Then_TheAnswerIsOther()
    {
        // Arrange: a framed reply whose body is not a DNS message
        var (dnsHost, dnsPort) = await StartDnsServerAsync(_ => [0x00, 0x04, 0x01, 0x02]);
        _nodeOptions.Tor.SocksProxy = _proxy.EndPoint;
        _nodeOptions.Bootstrap.TorNameServer = $"{dnsHost}:{dnsPort}";
        _route = (_, _) => new IPEndPoint(IPAddress.Loopback, dnsPort);
        var lookup = CreateLookup();

        // Act
        var response = await lookup.QueryAsync("nodes.lightning.directory", DnsRecordKind.Srv,
                                               TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(DnsLookupStatus.Other, response.Status);
    }

    [Fact]
    public async Task Given_AResolverThatNeverAnswers_When_Queried_Then_ItTimesOut()
    {
        // Arrange: a server that takes the connection and stays silent
        var (dnsHost, dnsPort) = await StartSilentServerAsync();
        _nodeOptions.Tor.SocksProxy = _proxy.EndPoint;
        _nodeOptions.Tor.ConnectTimeout = TimeSpan.FromMilliseconds(300);
        _nodeOptions.Bootstrap.TorNameServer = $"{dnsHost}:{dnsPort}";
        _route = (_, _) => new IPEndPoint(IPAddress.Loopback, dnsPort);
        var lookup = CreateLookup();

        // Act
        var response = await lookup.QueryAsync("nodes.lightning.directory", DnsRecordKind.Srv,
                                               TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(DnsLookupStatus.Timeout, response.Status);
    }

    [Fact]
    public async Task Given_TorOff_When_Queried_Then_ItIsNotAvailableAndNothingIsDialed()
    {
        // Arrange
        _nodeOptions.Tor.Mode = TorMode.Off;
        _nodeOptions.Tor.SocksProxy = _proxy.EndPoint;
        _nodeOptions.Bootstrap.TorNameServer = "9.9.9.9:53";
        var lookup = CreateLookup();

        // Act
        var response = await lookup.QueryAsync("nodes.lightning.directory", DnsRecordKind.Srv,
                                               TestContext.Current.CancellationToken);

        // Assert
        Assert.False(lookup.IsAvailable);
        Assert.Equal(DnsLookupStatus.Other, response.Status);
        Assert.Empty(_proxy.Requests);
    }

    [Fact]
    public async Task Given_AnEmptyNameServer_When_Queried_Then_ItIsNotAvailableAndNothingIsDialed()
    {
        // Arrange: the operator switched the Tor resolver off
        _nodeOptions.Tor.SocksProxy = _proxy.EndPoint;
        _nodeOptions.Bootstrap.TorNameServer = " ";
        var lookup = CreateLookup();

        // Act
        var response = await lookup.QueryAsync("nodes.lightning.directory", DnsRecordKind.Srv,
                                               TestContext.Current.CancellationToken);

        // Assert
        Assert.False(lookup.IsAvailable);
        Assert.Equal(DnsLookupStatus.Other, response.Status);
        Assert.Empty(_proxy.Requests);
    }

    public async ValueTask DisposeAsync() => await _proxy.DisposeAsync();

    /// <summary>The default route of the fake proxy: refuse.</summary>
    private static IPEndPoint? KeepRoute(string host, int port) => null;

    private TorSocksDnsRecordLookup CreateLookup() =>
        new(new TorSocksDialer(NullLogger<TorSocksDialer>.Instance, Options.Create(_nodeOptions)),
            Options.Create(_nodeOptions), NullLogger<TorSocksDnsRecordLookup>.Instance);

    /// <summary>
    /// A framed reply to whatever id the query carried: one SRV record over a plain target and one A record as glue.
    /// </summary>
    private static Func<byte[], byte[]> Response()
    {
        const string target = "ln1qz.target";

        return queryId =>
        {
            var message = new MemoryStream();
            message.Write([queryId[0], queryId[1], 0x81, 0x80, 0, 1, 0, 1, 0, 0, 0, 1]);
            WriteName(message, "nodes.lightning.directory");
            message.Write([0x00, 0x21, 0x00, 0x01]);                       // SRV IN
            message.Write([0xC0, 0x0C, 0x00, 0x21, 0x00, 0x01]);           // answer: the question's name, SRV IN
            message.Write([0x00, 0x00, 0x00, 0x3C]);
            var dataLength = 6 + WireNameLength(target);
            message.Write([(byte)(dataLength >> 8), (byte)dataLength]);
            message.Write([0x00, 0x0A, 0x00, 0x0A, 0x26, 0x07]);           // priority 10, weight 10, port 9735
            WriteName(message, target);
            message.Write([0xC0, 0x0C, 0x00, 0x01, 0x00, 0x01]);           // additional: the question's name, A IN
            message.Write([0x00, 0x00, 0x00, 0x3C, 0x00, 0x04, 9, 9, 9, 9]);
            var body = message.ToArray();
            var framed = new byte[body.Length + 2];
            BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)body.Length);
            body.CopyTo(framed, 2);
            return framed;
        };
    }

    private static int WireNameLength(string name) =>
        name.Length + name.Split('.').Length + 1; // each label's length byte, and the root's zero

    private static void WriteName(MemoryStream message, string name)
    {
        foreach (var label in name.Split('.'))
        {
            message.Write([(byte)label.Length]);
            var bytes = new byte[label.Length];
            for (var i = 0; i < label.Length; i++)
                bytes[i] = (byte)label[i];

            message.Write(bytes);
        }

        message.Write([0x00]);
    }

    /// <summary>A DNS-over-TCP server: reads one query and answers with <paramref name="reply"/> (the query's id).</summary>
    private static async Task<(string Host, int Port)> StartDnsServerAsync(Func<byte[], byte[]> reply)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(cts.Token);
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            var stream = client.GetStream();
                            var header = new byte[2];
                            await stream.ReadExactlyAsync(header, cts.Token);
                            var query = new byte[BinaryPrimitives.ReadUInt16BigEndian(header)];
                            if (query.Length == 0)
                                return;

                            await stream.ReadExactlyAsync(query, cts.Token);
                            await stream.WriteAsync(reply(query), cts.Token);
                        }
                    }, cts.Token);
                }
            }
            catch (Exception)
            {
                // Stopping
            }
        }, CancellationToken.None);

        return ("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
    }

    /// <summary>A DNS-over-TCP server that accepts and never answers.</summary>
    private static Task<(string Host, int Port)> StartSilentServerAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(cts.Token);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var header = new byte[2];
                            await client.GetStream().ReadExactlyAsync(header, cts.Token);
                            await Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None);
                        }
                        catch (Exception)
                        {
                            // The client gave up
                        }
                        finally
                        {
                            client.Dispose();
                        }
                    }, CancellationToken.None);
                }
            }
            catch (Exception)
            {
                // Stopping
            }
        }, CancellationToken.None);

        return Task.FromResult(("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port));
    }
}