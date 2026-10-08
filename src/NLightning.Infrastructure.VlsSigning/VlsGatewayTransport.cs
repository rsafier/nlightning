using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NLightning.Domain.Exceptions;

namespace NLightning.Infrastructure.VlsSigning;

/// <summary>One bounded authenticated request per Unix stream, with no implicit retries.</summary>
public sealed class VlsGatewayTransport
{
    public const int MaxFrameBytes = 1024 * 1024;
    private readonly string _socketPath;
    private readonly string _token;
    private readonly int _timeoutSeconds;
    public VlsGatewayTransport(string socketPath, string tokenFile) : this(socketPath, ReadCredential(tokenFile), 15) { }
    public VlsGatewayTransport(string socketPath, string token, int timeoutSeconds)
    {
        if (!Path.IsPathFullyQualified(socketPath)) throw new ArgumentException("VLS socket path must be absolute.");
        if (token.Length is < 32 or > 256 || token.Any(c => c > 127)) throw new ArgumentException("Invalid VLS credential.");
        if (timeoutSeconds is < 1 or > 300) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        _socketPath = socketPath; _token = token; _timeoutSeconds = timeoutSeconds;
    }
    public static string ReadCredential(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("VLS credential path must be absolute.");
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Length is < 32 or > 256)
            throw new ArgumentException("VLS credential must be a regular bounded file.");
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new ArgumentException("VLS credential file must be owner-only.");
        return File.ReadAllText(path);
    }
    public JsonObject Invoke(Guid id, JsonObject command) => JsonNode.Parse(InvokeBytes(id, command))!.AsObject();
    public byte[] InvokeBytes(Guid id, JsonObject command)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(_timeoutSeconds));
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), cancellation.Token).AsTask().GetAwaiter().GetResult();
        using var stream = new NetworkStream(socket, ownsSocket: false);
        var request = JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["token"] = _token, ["id"] = id.ToString("N"), ["command"] = command.DeepClone() });
        if (request.Length >= MaxFrameBytes) throw new ArgumentException("VLS request exceeds frame limit.");
        stream.WriteAsync(request, cancellation.Token).AsTask().GetAwaiter().GetResult();
        stream.WriteAsync(new byte[] { 10 }, cancellation.Token).AsTask().GetAwaiter().GetResult();
        using var response = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var count = stream.ReadAsync(chunk, cancellation.Token).AsTask().GetAwaiter().GetResult();
            if (count == 0) throw new IOException("VLS gateway closed before replying.");
            var newline = Array.IndexOf(chunk, (byte)10, 0, count);
            response.Write(chunk, 0, newline < 0 ? count : newline);
            if (response.Length > MaxFrameBytes) throw new IOException("VLS response exceeds frame limit.");
            if (newline >= 0) break;
        }
        using var document = JsonDocument.Parse(response.ToArray());
        if (!document.RootElement.GetProperty("ok").GetBoolean())
            throw new SignerException("VLS refused operation: " + document.RootElement.GetProperty("error").GetString());
        // Preserve the gateway's exact result bytes for durable receipt comparison.
        return Encoding.UTF8.GetBytes(document.RootElement.GetProperty("result").GetRawText());
    }
}