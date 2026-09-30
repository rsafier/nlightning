using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace NLightning.Infrastructure.Transport.Tor;

/// <summary>
/// A client of Tor's control port (control-spec): <c>PROTOCOLINFO</c>, authentication (HASHEDPASSWORD, SAFECOOKIE, or
/// NULL only when allowed; never plain COOKIE), <c>GETINFO</c>, <c>ADD_ONION</c> and <c>DEL_ONION</c>. One command at
/// a time; asynchronous events (6xx) are skipped.
/// </summary>
/// <remarks>
/// <para>An onion service added without <c>Flags=Detach</c> belongs to this connection: Tor removes it when the
/// connection closes, so a node that dies takes its service down with it and a node that restarts adds it again.</para>
/// <para>A command interrupted halfway (cancelled, an I/O error, a malformed reply) leaves an unknown part of its reply
/// unread, so the client is faulted from then on and every later command fails (NL-582): a stale reply is never taken
/// for the next command's. Reply lines are bounded while they are read (NL-589).</para>
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
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly byte[] _buffer = new byte[4096];

    private int _bufferOffset;
    private int _bufferCount;
    private volatile bool _faulted;

    /// <summary>
    /// Wraps a stream connected to the control port.
    /// </summary>
    /// <param name="stream">The connection.</param>
    /// <param name="owner">Disposed with the client (the socket behind the stream), if any.</param>
    public TorControlClient(Stream stream, IDisposable? owner = null)
    {
        _stream = stream;
        _owner = owner;
    }

    /// <summary>True once a command was interrupted halfway; the connection is then unusable (NL-582).</summary>
    public bool IsFaulted => _faulted;

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
            ThrowIfFaulted();
            await _stream.WriteAsync(Encoding.Latin1.GetBytes(command + "\r\n"), cancellationToken);
            await _stream.FlushAsync(cancellationToken);
            while (true)
            {
                var reply = await ReadReplyAsync(cancellationToken);
                if (reply.Status is < 600 or > 699)
                    return reply;
            }
        }
        catch (Exception e) when (!_faulted || e is IOException)
        {
            // Whatever of the reply is left unread would be taken for the next command's (NL-582)
            _faulted = true;
            if (e is IOException)
                throw new TorControlException("The Tor control connection failed", e);

            throw;
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
            ThrowIfFaulted();
            while (true)
                _ = await ReadReplyAsync(cancellationToken);
        }
        catch (Exception e) when (e is TorControlException or IOException)
        {
            // Closed
            _faulted = true;
        }
        catch
        {
            _faulted = true;
            throw;
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
    /// Authenticates: with the password when one is given (HASHEDPASSWORD), else with the cookie by SAFECOOKIE, which
    /// also proves that the other end is the Tor that wrote the cookie, else with no credentials (NULL) when
    /// <paramref name="allowUnauthenticated"/> is set (NL-575).
    /// </summary>
    /// <remarks>
    /// Plain COOKIE is never used: it sends the file's bytes to whatever listens on the control port, and a port that
    /// offers only COOKIE (every Tor since 0.2.3.13 offers SAFECOOKIE with it) could name any 32-byte file of ours.
    /// NULL authenticates neither side, so without the explicit option a control port that asks for nothing is
    /// refused before our onion service key is ever sent to it.
    /// </remarks>
    /// <param name="protocolInfo">What <see cref="GetProtocolInfoAsync"/> returned.</param>
    /// <param name="password">The control password, if configured.</param>
    /// <param name="cookieFile">A cookie file to read instead of the one Tor names.</param>
    /// <param name="allowUnauthenticated">Accept a control port that offers only NULL
    /// (<c>Node:Tor:AllowUnauthenticatedControlPort</c>).</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <exception cref="TorControlException">No usable method, or Tor refused the credentials.</exception>
    public async Task AuthenticateAsync(TorProtocolInfo protocolInfo, string? password, string? cookieFile,
                                        bool allowUnauthenticated, CancellationToken cancellationToken)
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
        else if (methods.Contains("SAFECOOKIE"))
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

            command = $"AUTHENTICATE {await SafeCookieResponseAsync(cookie, cancellationToken)}";
        }
        else if (methods.Contains("COOKIE"))
        {
            throw new TorControlException("The control port offers COOKIE but not SAFECOOKIE authentication, which "
                                        + "every maintained Tor offers; refusing to send the cookie to it (set "
                                        + "Node:Tor:ControlPassword to use a password instead)");
        }
        else if (methods.Contains("NULL"))
        {
            if (!allowUnauthenticated)
                throw new TorControlException("Tor's control port asks for no authentication, so it cannot prove it is "
                                            + "Tor: enable CookieAuthentication 1 in torrc (or use a ControlSocket), or "
                                            + "set Node:Tor:AllowUnauthenticatedControlPort true");

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
        _faulted = true;
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
                throw Malformed(line);

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
                    throw Malformed(line);
            }
        }
    }

    /// <summary>
    /// Reads one line (Latin-1, without its CRLF or LF), never holding more than <see cref="MaxLineLength"/> bytes of
    /// it (NL-589).
    /// </summary>
    private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = new List<byte>();
        while (true)
        {
            if (_bufferCount == 0)
            {
                _bufferOffset = 0;
                _bufferCount = await _stream.ReadAsync(_buffer, cancellationToken);
                if (_bufferCount == 0)
                    throw new TorControlException("Tor closed the control connection");
            }

            var available = _buffer.AsSpan(_bufferOffset, _bufferCount);
            var newLine = available.IndexOf((byte)'\n');
            var take = newLine < 0 ? available.Length : newLine;
            if (line.Count + take > MaxLineLength)
                throw new TorControlException("Control reply line too long");

            line.AddRange(available[..take]);
            var consumed = newLine < 0 ? take : take + 1;
            _bufferOffset += consumed;
            _bufferCount -= consumed;
            if (newLine < 0)
                continue;

            if (line.Count > 0 && line[^1] == (byte)'\r')
                line.RemoveAt(line.Count - 1);

            return Encoding.Latin1.GetString(line.ToArray());
        }
    }

    private static TorControlException Malformed(string line) =>
        new($"Malformed control reply line '{TorControlReply.Redact(line)}'");

    private void ThrowIfFaulted()
    {
        if (_faulted)
            throw new TorControlException("The Tor control connection is unusable after an interrupted command; "
                                        + "reconnect");
    }
}