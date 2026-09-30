using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NLightning.Tests.Utils.Mocks;

/// <summary>
/// An in-process SOCKS5 proxy on a loopback port (RFC 1928 CONNECT, RFC 1929 username/password), standing in for Tor's
/// <c>SocksPort</c>: every request is recorded, <see cref="Route"/> maps the requested host (an onion name) to a local
/// endpoint whose bytes are then piped both ways, and <see cref="ReplyCode"/> makes it refuse like Tor does.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class FakeSocks5Proxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentBag<TcpClient> _clients = [];
    private readonly Task _acceptLoop;

    public FakeSocks5Proxy()
    {
        _listener.Start();
        _acceptLoop = AcceptAsync();
    }

    /// <summary>The proxy's endpoint as <c>127.0.0.1:port</c>.</summary>
    public string EndPoint => $"127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    /// <summary>Require username/password authentication (Tor accepts both; isolation needs credentials).</summary>
    public bool RequireAuthentication { get; init; }

    /// <summary>Where a requested host:port really goes; null refuses with host unreachable (4).</summary>
    public Func<string, int, IPEndPoint?> Route { get; init; } = (_, _) => null;

    /// <summary>A reply code to send instead of connecting (e.g. 0xF0, Tor's descriptor not found).</summary>
    public byte? ReplyCode { get; init; }

    /// <summary>Every CONNECT request, in order.</summary>
    public ConcurrentQueue<Socks5Request> Requests { get; } = new();

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        foreach (var client in _clients)
            client.Dispose();

        try
        {
            await _acceptLoop;
        }
        catch (Exception)
        {
            // Stopping
        }

        _cts.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception)
            {
                return;
            }

            _clients.Add(client);
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var ct = _cts.Token;

            // Greeting
            var head = await ReadAsync(stream, 2, ct);
            var methods = await ReadAsync(stream, head[1], ct);
            var wanted = RequireAuthentication ? (byte)0x02 : (byte)0x00;
            if (!methods.Contains(wanted) && !(wanted == 0x00 && methods.Contains((byte)0x02)))
            {
                await stream.WriteAsync(new byte[] { 0x05, 0xFF }, ct);
                return;
            }

            var chosen = methods.Contains(wanted) ? wanted : (byte)0x02;
            await stream.WriteAsync(new byte[] { 0x05, chosen }, ct);

            string? username = null;
            string? password = null;
            if (chosen == 0x02)
            {
                var version = await ReadAsync(stream, 1, ct);
                var userLength = await ReadAsync(stream, 1, ct);
                username = Encoding.UTF8.GetString(await ReadAsync(stream, userLength[0], ct));
                var passLength = await ReadAsync(stream, 1, ct);
                password = Encoding.UTF8.GetString(await ReadAsync(stream, passLength[0], ct));
                await stream.WriteAsync(new byte[] { version[0], 0x00 }, ct);
            }

            // Request
            var request = await ReadAsync(stream, 4, ct);
            string host;
            switch (request[3])
            {
                case 0x01:
                    host = new IPAddress(await ReadAsync(stream, 4, ct)).ToString();
                    break;
                case 0x04:
                    host = new IPAddress(await ReadAsync(stream, 16, ct)).ToString();
                    break;
                case 0x03:
                    var length = await ReadAsync(stream, 1, ct);
                    host = Encoding.ASCII.GetString(await ReadAsync(stream, length[0], ct));
                    break;
                default:
                    await ReplyAsync(stream, 0x08, ct);
                    return;
            }

            var port = BinaryPrimitives.ReadUInt16BigEndian(await ReadAsync(stream, 2, ct));
            Requests.Enqueue(new Socks5Request(host, port, request[3], username, password));

            if (ReplyCode is { } code)
            {
                await ReplyAsync(stream, code, ct);
                return;
            }

            var target = Route(host, port);
            if (target is null)
            {
                await ReplyAsync(stream, 0x04, ct);
                return;
            }

            var upstream = new TcpClient();
            _clients.Add(upstream);
            try
            {
                await upstream.ConnectAsync(target, ct);
            }
            catch (SocketException)
            {
                await ReplyAsync(stream, 0x05, ct);
                return;
            }

            await ReplyAsync(stream, 0x00, ct);
            var upstreamStream = upstream.GetStream();
            await Task.WhenAny(stream.CopyToAsync(upstreamStream, ct), upstreamStream.CopyToAsync(stream, ct));
            upstream.Dispose();
        }
        catch (Exception)
        {
            // A closed connection
        }
        finally
        {
            client.Dispose();
        }
    }

    private static Task ReplyAsync(Stream stream, byte code, CancellationToken ct) =>
        stream.WriteAsync(new byte[] { 0x05, code, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct).AsTask();

    private static async Task<byte[]> ReadAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, ct);
        return buffer;
    }
}

/// <summary>One CONNECT request seen by <see cref="FakeSocks5Proxy"/>.</summary>
/// <param name="Host">The host as sent (a domain name for ATYP 3).</param>
/// <param name="Port">The port.</param>
/// <param name="AddressType">1 IPv4, 3 domain name, 4 IPv6.</param>
/// <param name="Username">The RFC 1929 username, if authenticated.</param>
/// <param name="Password">The RFC 1929 password, if authenticated.</param>
public sealed record Socks5Request(string Host, int Port, byte AddressType, string? Username, string? Password);