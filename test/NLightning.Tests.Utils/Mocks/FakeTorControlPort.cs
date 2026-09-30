using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace NLightning.Tests.Utils.Mocks;

using Domain.Gossip.Addresses;

/// <summary>
/// An in-process stand-in for Tor's control port on a loopback port: <c>PROTOCOLINFO</c>, SAFECOOKIE/COOKIE/password/
/// NULL authentication against a cookie file it writes, <c>GETINFO version</c>, <c>ADD_ONION</c> (a new key gets a
/// random valid v3 address; a key it issued keeps its address) and <c>DEL_ONION</c>. Every command is recorded;
/// <see cref="DropConnections"/> behaves like a Tor restart.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class FakeTorControlPort : IAsyncDisposable
{
    private const string ServerToControllerKey = "Tor safe cookie authentication server-to-controller hash";
    private const string ControllerToServerKey = "Tor safe cookie authentication controller-to-server hash";

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly ConcurrentDictionary<string, string> _serviceIdsByKey = new();
    private readonly Task _acceptLoop;
    private readonly string _directory;

    public FakeTorControlPort(string authMethods = "COOKIE,SAFECOOKIE", string? password = null)
    {
        AuthMethods = authMethods;
        Password = password;
        _directory = Path.Combine(Path.GetTempPath(), $"nltg-faketor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        CookieFile = Path.Combine(_directory, "control_auth_cookie");
        File.WriteAllBytes(CookieFile, Cookie);
        _listener.Start();
        _acceptLoop = AcceptAsync();
    }

    /// <summary>The control port as <c>127.0.0.1:port</c>.</summary>
    public string EndPoint => $"127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    /// <summary>The cookie file named in <c>PROTOCOLINFO</c>.</summary>
    public string CookieFile { get; }

    /// <summary>The cookie.</summary>
    public byte[] Cookie { get; } = RandomNumberGenerator.GetBytes(32);

    /// <summary>The <c>METHODS=</c> list.</summary>
    public string AuthMethods { get; }

    /// <summary>The control password, for HASHEDPASSWORD.</summary>
    public string? Password { get; }

    /// <summary>The version reported.</summary>
    public string TorVersion { get; init; } = "0.4.8.13";

    /// <summary>Send an asynchronous event line before every reply (a controller must skip it).</summary>
    public bool EmitEvents { get; init; }

    /// <summary>Reply to <c>ADD_ONION</c> with this error line instead of adding the service.</summary>
    public string? AddOnionError { get; set; }

    /// <summary>Every command line received, in order.</summary>
    public ConcurrentQueue<string> Commands { get; } = new();

    /// <summary>Services added and not deleted, by service id.</summary>
    public ConcurrentDictionary<string, string> ActiveServices { get; } = new();

    /// <summary>Number of connections accepted.</summary>
    public int ConnectionCount;

    /// <summary>Closes every control connection (Tor restarting); non-detached services go with them.</summary>
    public void DropConnections()
    {
        foreach (var client in _clients.Keys)
            client.Dispose();

        ActiveServices.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        DropConnections();
        try
        {
            await _acceptLoop;
        }
        catch (Exception)
        {
            // Stopping
        }

        _cts.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort
        }
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

            Interlocked.Increment(ref ConnectionCount);
            _clients[client] = 0;
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        var owned = new List<string>();
        try
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.Latin1, false, 1024, leaveOpen: true);
            var authenticated = false;
            byte[]? clientNonce = null;
            byte[]? serverNonce = null;
            while (await reader.ReadLineAsync(_cts.Token) is { } line)
            {
                Commands.Enqueue(line);
                var reply = Handle(line, ref authenticated, ref clientNonce, ref serverNonce, owned);
                if (EmitEvents)
                    reply = "650 CIRC 1 LAUNCHED\r\n" + reply;

                await stream.WriteAsync(Encoding.Latin1.GetBytes(reply), _cts.Token);
            }
        }
        catch (Exception)
        {
            // Closed
        }
        finally
        {
            _clients.TryRemove(client, out _);
            client.Dispose();
            foreach (var serviceId in owned)
                ActiveServices.TryRemove(serviceId, out _);
        }
    }

    private string Handle(string line, ref bool authenticated, ref byte[]? clientNonce, ref byte[]? serverNonce,
                          List<string> owned)
    {
        var verb = line.Split(' ')[0].ToUpperInvariant();
        switch (verb)
        {
            case "PROTOCOLINFO":
                return "250-PROTOCOLINFO 1\r\n"
                     + $"250-AUTH METHODS={AuthMethods} COOKIEFILE=\"{CookieFile.Replace("\\", "\\\\")}\"\r\n"
                     + $"250-VERSION Tor=\"{TorVersion}\"\r\n250 OK\r\n";
            case "AUTHCHALLENGE":
                {
                    clientNonce = Convert.FromHexString(line.Split(' ')[2]);
                    serverNonce = RandomNumberGenerator.GetBytes(32);
                    var serverHash = Hmac(ServerToControllerKey, clientNonce, serverNonce);
                    return $"250 AUTHCHALLENGE SERVERHASH={Convert.ToHexString(serverHash)} "
                         + $"SERVERNONCE={Convert.ToHexString(serverNonce)}\r\n";
                }
            case "AUTHENTICATE":
                {
                    var argument = line.Length > 13 ? line[13..].Trim() : string.Empty;
                    var ok = argument.StartsWith('"')
                                 ? Password is not null && argument == $"\"{Password}\""
                                 : argument.Length == 0
                                     ? AuthMethods.Contains("NULL")
                                     : clientNonce is not null && serverNonce is not null
                                         ? argument == Convert.ToHexString(Hmac(ControllerToServerKey, clientNonce,
                                                                                serverNonce))
                                         : argument == Convert.ToHexString(Cookie);
                    authenticated = ok;
                    return ok ? "250 OK\r\n" : "515 Authentication failed\r\n";
                }
        }

        if (!authenticated)
            return "514 Authentication required.\r\n";

        switch (verb)
        {
            case "GETINFO":
                return $"250-version={TorVersion}\r\n250 OK\r\n";
            case "ADD_ONION":
                {
                    if (AddOnionError is not null)
                        return AddOnionError + "\r\n";

                    var keySpec = line.Split(' ')[1];
                    string? newKey = null;
                    if (keySpec == "NEW:ED25519-V3")
                    {
                        newKey = "ED25519-V3:" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
                        keySpec = newKey;
                    }

                    var serviceId = _serviceIdsByKey.GetOrAdd(keySpec, _ =>
                    {
                        var host = OnionV3Address.ToHostName(
                            OnionV3Address.FromPublicKey(RandomNumberGenerator.GetBytes(32)));
                        return host[..^OnionV3Address.Suffix.Length];
                    });
                    ActiveServices[serviceId] = line;
                    owned.Add(serviceId);
                    return $"250-ServiceID={serviceId}\r\n"
                         + (newKey is null ? string.Empty : $"250-PrivateKey={newKey}\r\n") + "250 OK\r\n";
                }
            case "DEL_ONION":
                {
                    var serviceId = line.Split(' ')[1];
                    return ActiveServices.TryRemove(serviceId, out _) ? "250 OK\r\n" : "552 Unknown Onion Service id\r\n";
                }
            default:
                return "510 Unrecognized command\r\n";
        }
    }

    private byte[] Hmac(string key, byte[] clientNonce, byte[] serverNonce) =>
        HMACSHA256.HashData(Encoding.ASCII.GetBytes(key), (byte[])[.. Cookie, .. clientNonce, .. serverNonce]);
}