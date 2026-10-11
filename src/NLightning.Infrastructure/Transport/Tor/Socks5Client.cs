using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NLightning.Infrastructure.Transport.Tor;

/// <summary>
/// The client side of a SOCKS5 <c>CONNECT</c> (RFC 1928) with optional username/password authentication (RFC 1929),
/// as Tor's <c>SocksPort</c> speaks it. A host name is sent as a domain name (ATYP 3) and resolved by the proxy, never
/// locally; that is how <c>.onion</c> names reach Tor.
/// </summary>
public static class Socks5Client
{
    private const byte Version = 0x05;
    private const byte AuthVersion = 0x01;
    private const byte MethodNoAuth = 0x00;
    private const byte MethodUserPass = 0x02;
    private const byte MethodNoneAcceptable = 0xFF;
    private const byte CommandConnect = 0x01;
    private const byte AddressIPv4 = 0x01;
    private const byte AddressDomain = 0x03;
    private const byte AddressIPv6 = 0x04;

    /// <summary>
    /// Asks the proxy at the other end of <paramref name="stream"/> to connect to <paramref name="host"/>:
    /// <paramref name="port"/>. On return the stream carries the connection to the target.
    /// </summary>
    /// <param name="stream">A stream connected to the proxy, nothing sent yet.</param>
    /// <param name="host">An IP address, or a host name the proxy resolves (a <c>.onion</c> name for Tor).</param>
    /// <param name="port">The target port.</param>
    /// <param name="credentials">Username and password (RFC 1929), each 1-255 bytes; Tor isolates streams by them
    /// (<c>IsolateSOCKSAuth</c>). Null offers no authentication.</param>
    /// <param name="cancellationToken">Cancels the handshake; the caller then closes the stream.</param>
    /// <exception cref="Socks5Exception">The proxy refused the request or broke the protocol.</exception>
    public static async Task ConnectAsync(Stream stream, string host, int port,
                                          (string Username, string Password)? credentials,
                                          CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrEmpty(host);
        if (port is < 1 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(port), port, "The port must be from 1 to 65535.");

        // Greeting: offer exactly the one method we mean to use, so a proxy cannot pick an unauthenticated stream when
        // we asked for isolation
        var method = credentials is null ? MethodNoAuth : MethodUserPass;
        await stream.WriteAsync(new byte[] { Version, 1, method }, cancellationToken);
        var choice = await ReadExactAsync(stream, 2, cancellationToken);
        if (choice[0] != Version)
            throw new Socks5Exception($"The proxy answered SOCKS version {choice[0]}, not 5 (is it a SOCKS5 port?)");
        if (choice[1] == MethodNoneAcceptable)
            throw new Socks5Exception(credentials is null
                                          ? "The proxy requires authentication"
                                          : "The proxy does not accept username/password authentication");
        if (choice[1] != method)
            throw new Socks5Exception($"The proxy chose authentication method {choice[1]}, which was not offered");

        if (credentials is { } auth)
            await AuthenticateAsync(stream, auth.Username, auth.Password, cancellationToken);

        await stream.WriteAsync(BuildConnectRequest(host, port), cancellationToken);
        await ReadConnectReplyAsync(stream, host, port, cancellationToken);
    }

    /// <summary>
    /// The <c>CONNECT</c> request: an IP literal as ATYP 1/4, anything else as a domain name (ATYP 3).
    /// </summary>
    internal static byte[] BuildConnectRequest(string host, int port)
    {
        byte[] address;
        byte addressType;
        if (IPAddress.TryParse(host, out var ip))
        {
            address = ip.GetAddressBytes();
            addressType = ip.AddressFamily == AddressFamily.InterNetworkV6 ? AddressIPv6 : AddressIPv4;
        }
        else
        {
            var name = Encoding.ASCII.GetBytes(host);
            if (name.Length > 255 || host.Any(c => c > 0x7F))
                throw new ArgumentException($"'{host}' is not an ASCII host name of at most 255 bytes.", nameof(host));

            address = new byte[1 + name.Length];
            address[0] = (byte)name.Length;
            name.CopyTo(address, 1);
            addressType = AddressDomain;
        }

        var request = new byte[4 + address.Length + 2];
        request[0] = Version;
        request[1] = CommandConnect;
        request[2] = 0x00;
        request[3] = addressType;
        address.CopyTo(request, 4);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4 + address.Length), (ushort)port);
        return request;
    }

    private static async Task AuthenticateAsync(Stream stream, string username, string password,
                                                CancellationToken cancellationToken)
    {
        var user = Encoding.UTF8.GetBytes(username);
        var pass = Encoding.UTF8.GetBytes(password);
        if (user.Length is 0 or > 255 || pass.Length is 0 or > 255)
            throw new ArgumentException("SOCKS5 username and password are 1-255 bytes each.");

        var request = new byte[3 + user.Length + pass.Length];
        request[0] = AuthVersion;
        request[1] = (byte)user.Length;
        user.CopyTo(request, 2);
        request[2 + user.Length] = (byte)pass.Length;
        pass.CopyTo(request, 3 + user.Length);
        await stream.WriteAsync(request, cancellationToken);

        // RFC 1929: VER (0x01, the subnegotiation's version) and STATUS (NL-587)
        var reply = await ReadExactAsync(stream, 2, cancellationToken);
        if (reply[0] != AuthVersion)
            throw new Socks5Exception($"The proxy answered the SOCKS5 credentials with version {reply[0]}, not 1");
        if (reply[1] != 0x00)
            throw new Socks5Exception($"The proxy refused the SOCKS5 credentials (status {reply[1]})");
    }

    private static async Task ReadConnectReplyAsync(Stream stream, string host, int port,
                                                    CancellationToken cancellationToken)
    {
        var head = await ReadExactAsync(stream, 4, cancellationToken);
        if (head[0] != Version)
            throw new Socks5Exception($"The proxy answered SOCKS version {head[0]} to CONNECT, not 5");
        if (head[1] != 0x00)
            throw new Socks5Exception(head[1], $"The proxy could not connect to {host}:{port}: "
                                             + Socks5Exception.Describe(head[1]));

        // BND.ADDR and BND.PORT follow; read them all so nothing of the proxy's reply is left for the tunnel's reader
        var remaining = head[3] switch
        {
            AddressIPv4 => 4 + 2,
            AddressIPv6 => 16 + 2,
            AddressDomain => (await ReadExactAsync(stream, 1, cancellationToken))[0] + 2,
            _ => throw new Socks5Exception($"The proxy's CONNECT reply has unknown address type {head[3]}")
        };
        _ = await ReadExactAsync(stream, remaining, cancellationToken);
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        try
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken);
        }
        catch (EndOfStreamException e)
        {
            throw new Socks5Exception("The proxy closed the connection during the SOCKS5 handshake", e);
        }

        return buffer;
    }
}