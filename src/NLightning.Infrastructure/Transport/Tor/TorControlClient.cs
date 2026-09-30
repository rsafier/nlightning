using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace NLightning.Infrastructure.Transport.Tor;

/// <summary>
/// A client of Tor's control port (control-spec): <c>PROTOCOLINFO</c>, authentication (SAFECOOKIE, COOKIE,
/// HASHEDPASSWORD or NULL, the strongest Tor offers that we can use), <c>GETINFO</c>, <c>ADD_ONION</c> and
/// <c>DEL_ONION</c>. One command at a time; asynchronous events (6xx) are skipped.
/// </summary>
/// <remarks>
/// An onion service added without <c>Flags=Detach</c> belongs to this connection: Tor removes it when the connection
/// closes, so a node that dies takes its service down with it and a node that restarts adds it again.
/// </remarks>
public sealed class TorControlClient : IAsyncDisposable
{
    private const string ServerToControllerKey = "Tor safe cookie authentication server-to-controller hash";
    private const string ControllerToServerKey = "Tor safe cookie authentication controller-to-server hash";
    private const int CookieLength = 32;
    private const int NonceLength = 32;
    private const int MaxLineLength = 64 * 1024;

    private readonly Stream _stream;
    private readonly IDisposable? _owner;
    private readonly StreamReader _reader;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Wraps a stream connected to the control port.
    /// </summary>
    /// <param name="stream">The connection.</param>
    /// <param name="owner">Disposed with the client (the socket behind the stream), if any.</param>
    public TorControlClient(Stream stream, IDisposable? owner = null)
    {
        _stream = stream;
        _owner = owner;
        _reader = new StreamReader(stream, Encoding.Latin1, false, 4096, leaveOpen: true);
    }

    /// <summary>
    /// Connects to the control port at <paramref name="endPoint"/> (TCP or Unix socket).
    /// </summary>
    public static async Task<TorControlClient> ConnectAsync(EndPoint endPoint, CancellationToken cancellationToken)
    {
        var client = await TorSocksDialer.ConnectToEndPointAsync(endPoint, cancellationToken);
        return new TorControlClient(client.GetStream(), client);
    }

    /// <summary>
    /// Sends one command line and reads its reply (skipping asynchronous events). Does not check the status.
    /// </summary>
    public async Task<TorControlReply> SendAsync(string command, CancellationToken cancellationToken)
    {
        if (command.Contains('\r') || command.Contains('\n'))
            throw new ArgumentException("A control command is one line.", nameof(command));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _stream.WriteAsync(Encoding.Latin1.GetBytes(command + "\r\n"), cancellationToken);
            await _stream.FlushAsync(cancellationToken);
            while (true)
            {
                var reply = await ReadReplyAsync(cancellationToken);
                if (reply.Status is < 600 or > 699)
                    return reply;
            }
        }
        catch (IOException e)
        {
            throw new TorControlException("The Tor control connection failed", e);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reads until Tor closes the connection or <paramref name="cancellationToken"/> is cancelled, skipping
    /// asynchronous events. Use it once no more commands will be sent, to notice Tor going away.
    /// </summary>
    public async Task WaitForCloseAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            while (true)
                _ = await ReadReplyAsync(cancellationToken);
        }
        catch (Exception e) when (e is TorControlException or IOException)
        {
            // Closed
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// <c>PROTOCOLINFO 1</c>: the authentication methods, the cookie file and Tor's version. Must come before
    /// authentication (Tor answers it once there).
    /// </summary>
    public async Task<TorProtocolInfo> GetProtocolInfoAsync(CancellationToken cancellationToken)
    {
        var reply = await SendAsync("PROTOCOLINFO 1", cancellationToken);
        if (!reply.IsOk)
            throw new TorControlException("PROTOCOLINFO failed", reply);

        return TorProtocolInfo.Parse(reply);
    }

    /// <summary>
    /// Authenticates with the strongest method both sides can use: a password when one is given (HASHEDPASSWORD),
    /// else the cookie by SAFECOOKIE (Tor proves it knows the cookie too), else COOKIE, else NULL.
    /// </summary>
    /// <param name="protocolInfo">What <see cref="GetProtocolInfoAsync"/> returned.</param>
    /// <param name="password">The control password, if configured.</param>
    /// <param name="cookieFile">A cookie file to read instead of the one Tor names.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <exception cref="TorControlException">No usable method, or Tor refused the credentials.</exception>
    public async Task AuthenticateAsync(TorProtocolInfo protocolInfo, string? password, string? cookieFile,
                                        CancellationToken cancellationToken)
    {
        var methods = protocolInfo.AuthMethods;
        string command;
        if (!string.IsNullOrEmpty(password))
        {
            if (!methods.Contains("HASHEDPASSWORD"))
                throw new TorControlException("Tor does not accept a control password (no HashedControlPassword in "
                                            + $"torrc); it offers {string.Join(", ", methods)}");

            command = $"AUTHENTICATE {Quote(password)}";
        }
        else if (methods.Contains("SAFECOOKIE") || methods.Contains("COOKIE"))
        {
            var path = string.IsNullOrWhiteSpace(cookieFile) ? protocolInfo.CookieFile : cookieFile;
            if (string.IsNullOrWhiteSpace(path))
                throw new TorControlException("Tor offers cookie authentication but names no cookie file");

            byte[] cookie;
            try
            {
                cookie = await File.ReadAllBytesAsync(path, cancellationToken);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new TorControlException($"Could not read Tor's control cookie {path} (is this user in Tor's "
                                            + "group, or CookieAuthFileGroupReadable set?)", e);
            }

            if (cookie.Length != CookieLength)
                throw new TorControlException($"Tor's control cookie {path} is {cookie.Length} bytes, not "
                                            + CookieLength.ToString(CultureInfo.InvariantCulture));

            command = methods.Contains("SAFECOOKIE")
                          ? $"AUTHENTICATE {await SafeCookieResponseAsync(cookie, cancellationToken)}"
                          : $"AUTHENTICATE {Convert.ToHexString(cookie)}";
        }
        else if (methods.Contains("NULL"))
        {
            command = "AUTHENTICATE";
        }
        else
        {
            throw new TorControlException("Tor's control port wants a password (HASHEDPASSWORD): set "
                                        + "Node:Tor:ControlPassword");
        }

        var reply = await SendAsync(command, cancellationToken);
        if (!reply.IsOk)
            throw new TorControlException("Tor refused our control port authentication", reply);
    }

    /// <summary>
    /// <c>GETINFO key</c>: the value (a data reply's lines joined with newlines).
    /// </summary>
    public async Task<string> GetInfoAsync(string key, CancellationToken cancellationToken)
    {
        var reply = await SendAsync($"GETINFO {key}", cancellationToken);
        if (!reply.IsOk)
            throw new TorControlException($"GETINFO {key} failed", reply);

        var prefix = key + "=";
        var index = reply.Lines.ToList().FindIndex(l => l.StartsWith(prefix, StringComparison.Ordinal));
        if (index < 0)
            throw new TorControlException($"GETINFO {key} returned no value", reply);

        // A data reply (250+key=) carries its value on the lines after the first one
        var first = reply.Lines[index][prefix.Length..];
        if (first.Length > 0)
            return first;

        return string.Join('\n', reply.Lines.Skip(index + 1).TakeWhile(l => l != "OK"));
    }

    /// <summary>
    /// <c>ADD_ONION</c> of a v3 service: a new key (<paramref name="keyBlob"/> null) or the saved one
    /// (<c>ED25519-V3:&lt;base64&gt;</c>), one virtual port to <paramref name="target"/>. Not detached: the service
    /// lives as long as this connection.
    /// </summary>
    /// <returns>The service id (the onion name without <c>.onion</c>) and, for a new key, its blob.</returns>
    public async Task<(string ServiceId, string? PrivateKey)> AddOnionAsync(string? keyBlob, int virtualPort,
                                                                            string target,
                                                                            CancellationToken cancellationToken)
    {
        if (keyBlob is not null && !keyBlob.StartsWith("ED25519-V3:", StringComparison.Ordinal))
            throw new ArgumentException("Only ED25519-V3 keys are supported.", nameof(keyBlob));
        if (target.Any(char.IsWhiteSpace) || keyBlob?.Any(char.IsWhiteSpace) == true)
            throw new ArgumentException("The key and target must not contain white space.");

        var keySpec = keyBlob ?? "NEW:ED25519-V3";
        var reply = await SendAsync(
            $"ADD_ONION {keySpec} Port={virtualPort.ToString(CultureInfo.InvariantCulture)},{target}",
            cancellationToken);
        if (!reply.IsOk)
            throw new TorControlException("ADD_ONION failed", reply);

        var serviceId = reply.GetValue("ServiceID")
                     ?? throw new TorControlException("ADD_ONION returned no ServiceID", reply);
        return (serviceId, reply.GetValue("PrivateKey"));
    }

    /// <summary>
    /// <c>DEL_ONION</c>; an unknown service (552) is ignored.
    /// </summary>
    public async Task DeleteOnionAsync(string serviceId, CancellationToken cancellationToken)
    {
        var reply = await SendAsync($"DEL_ONION {serviceId}", cancellationToken);
        if (!reply.IsOk && reply.Status != 552)
            throw new TorControlException("DEL_ONION failed", reply);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _reader.Dispose();
        await _stream.DisposeAsync();
        _owner?.Dispose();
        _gate.Dispose();
    }

    /// <summary>
    /// The SAFECOOKIE exchange (control-spec §3.24): <c>AUTHCHALLENGE SAFECOOKIE</c> with our nonce; Tor's hash is
    /// checked (Tor knows the cookie) before ours is returned for <c>AUTHENTICATE</c>.
    /// </summary>
    private async Task<string> SafeCookieResponseAsync(byte[] cookie, CancellationToken cancellationToken)
    {
        var clientNonce = RandomNumberGenerator.GetBytes(NonceLength);
        var reply = await SendAsync($"AUTHCHALLENGE SAFECOOKIE {Convert.ToHexString(clientNonce)}", cancellationToken);
        if (!reply.IsOk || reply.Lines.Count == 0)
            throw new TorControlException("AUTHCHALLENGE failed", reply);

        var fields = ParseKeyValues(reply.Lines[0]);
        if (!fields.TryGetValue("SERVERHASH", out var serverHashHex)
         || !fields.TryGetValue("SERVERNONCE", out var serverNonceHex))
            throw new TorControlException("AUTHCHALLENGE reply lacks SERVERHASH or SERVERNONCE", reply);

        byte[] serverHash;
        byte[] serverNonce;
        try
        {
            serverHash = Convert.FromHexString(serverHashHex);
            serverNonce = Convert.FromHexString(serverNonceHex);
        }
        catch (FormatException e)
        {
            throw new TorControlException("AUTHCHALLENGE reply is not hex", e);
        }

        var (expectedServerHash, clientHash) = ComputeSafeCookieHashes(cookie, clientNonce, serverNonce);
        if (!CryptographicOperations.FixedTimeEquals(expectedServerHash, serverHash))
            throw new TorControlException("Tor's SAFECOOKIE hash does not match the cookie: the control port is not "
                                        + "the Tor that wrote this cookie");

        return Convert.ToHexString(clientHash);
    }

    /// <summary>
    /// The two SAFECOOKIE HMAC-SHA256 values over <c>cookie || client nonce || server nonce</c>: Tor's (server to
    /// controller) and ours (controller to server).
    /// </summary>
    internal static (byte[] ServerHash, byte[] ClientHash) ComputeSafeCookieHashes(
        byte[] cookie, byte[] clientNonce, byte[] serverNonce)
    {
        var message = new byte[cookie.Length + clientNonce.Length + serverNonce.Length];
        cookie.CopyTo(message, 0);
        clientNonce.CopyTo(message, cookie.Length);
        serverNonce.CopyTo(message, cookie.Length + clientNonce.Length);

        return (HMACSHA256.HashData(Encoding.ASCII.GetBytes(ServerToControllerKey), message),
                HMACSHA256.HashData(Encoding.ASCII.GetBytes(ControllerToServerKey), message));
    }

    /// <summary>
    /// A control-spec QuotedString: in double quotes, backslash and double quote escaped.
    /// </summary>
    internal static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
      + "\"";

    /// <summary>
    /// Reads the <c>KEY=value</c> pairs of a reply line (values may be QuotedStrings).
    /// </summary>
    internal static Dictionary<string, string> ParseKeyValues(string line)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && line[i] == ' ')
                i++;

            var start = i;
            while (i < line.Length && line[i] != '=' && line[i] != ' ')
                i++;

            if (i >= line.Length || line[i] != '=')
                continue;

            var key = line[start..i];
            i++;
            string value;
            if (i < line.Length && line[i] == '"')
                (value, i) = ReadQuoted(line, i);
            else
            {
                var valueStart = i;
                while (i < line.Length && line[i] != ' ')
                    i++;

                value = line[valueStart..i];
            }

            result[key] = value;
        }

        return result;
    }

    /// <summary>
    /// Reads a QuotedString starting at <paramref name="start"/> (the opening quote), C escapes decoded.
    /// </summary>
    internal static (string Value, int Next) ReadQuoted(string line, int start)
    {
        var builder = new StringBuilder();
        var i = start + 1;
        while (i < line.Length && line[i] != '"')
        {
            if (line[i] == '\\' && i + 1 < line.Length)
            {
                i++;
                var c = line[i];
                if (c is >= '0' and <= '7')
                {
                    // Up to three octal digits (a byte of a non-ASCII path)
                    var octal = 0;
                    var digits = 0;
                    while (digits < 3 && i < line.Length && line[i] is >= '0' and <= '7')
                    {
                        octal = octal * 8 + (line[i] - '0');
                        i++;
                        digits++;
                    }

                    builder.Append((char)octal);
                    continue;
                }

                builder.Append(c switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => c
                });
                i++;
                continue;
            }

            builder.Append(line[i]);
            i++;
        }

        // The octal escapes are the raw bytes of a UTF-8 path
        var text = builder.ToString();
        var decoded = text.Any(ch => ch > 0x7F) ? Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(text)) : text;
        return (decoded, Math.Min(i + 1, line.Length));
    }

    /// <summary>
    /// Reads one reply: mid lines until the end line; a data line's block (up to a lone ".") is appended as lines,
    /// with dot-stuffing removed.
    /// </summary>
    private async Task<TorControlReply> ReadReplyAsync(CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        while (true)
        {
            var line = await ReadLineAsync(cancellationToken);
            if (line.Length < 4 || !int.TryParse(line.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture,
                                                 out var status))
                throw new TorControlException($"Malformed control reply line '{line}'");

            var separator = line[3];
            lines.Add(line[4..]);
            switch (separator)
            {
                case ' ':
                    return new TorControlReply(status, lines);
                case '-':
                    continue;
                case '+':
                    while (true)
                    {
                        var data = await ReadLineAsync(cancellationToken);
                        if (data == ".")
                            break;

                        lines.Add(data.StartsWith("..", StringComparison.Ordinal) ? data[1..] : data);
                    }

                    continue;
                default:
                    throw new TorControlException($"Malformed control reply line '{line}'");
            }
        }
    }

    private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = await _reader.ReadLineAsync(cancellationToken)
                ?? throw new TorControlException("Tor closed the control connection");
        if (line.Length > MaxLineLength)
            throw new TorControlException("Control reply line too long");

        return line;
    }
}