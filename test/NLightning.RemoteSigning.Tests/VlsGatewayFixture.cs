using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;

namespace NLightning.RemoteSigning.Tests;

/// <summary>Actual pinned Rust VLS gateway. Tests never substitute a native signer or build Rust implicitly.</summary>
public sealed class VlsGatewayFixture : IAsyncDisposable
{
    public const string BinaryVariable = "NLTG_VLS_GATEWAY_BINARY";
    public const string NodeToken = "vls-node-test-credential-00000000000000000000000000";
    public const string ApprovalToken = "vls-approval-test-credential-111111111111111111111";
    private readonly byte[] _seed;
    private Process? _process;
    private Task<string>? _stderr;
    private Task<string>? _stdout;
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "vls-" + Guid.NewGuid().ToString("N"));
    public string SocketPath => Path.Combine(DirectoryPath, "node.sock");
    public string ApprovalSocketPath => Path.Combine(DirectoryPath, "approval.sock");
    public string TokenFile => Path.Combine(DirectoryPath, "node-token");
    public string ApprovalTokenFile => Path.Combine(DirectoryPath, "approval-token");

    public VlsGatewayFixture(byte seedByte = 0x51) => _seed = Enumerable.Repeat(seedByte, 32).ToArray();

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The VLS gateway proof requires Unix sockets and permissions.");
        Directory.CreateDirectory(DirectoryPath);
        File.SetUnixFileMode(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        WriteCredential(TokenFile, NodeToken);
        WriteCredential(ApprovalTokenFile, ApprovalToken);
        await StartAsync(ct);
    }

    private static void WriteCredential(string path, string token)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("VLS credentials require Unix permissions.");
        File.WriteAllText(path, token);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private async Task StartAsync(CancellationToken ct)
    {
        var binary = Environment.GetEnvironmentVariable(BinaryVariable);
        if (string.IsNullOrWhiteSpace(binary) || !File.Exists(binary))
            throw new InvalidOperationException($"Set {BinaryVariable} to the pinned gateway binary built by tools/vls-gateway/run.sh build.");
        var start = new ProcessStartInfo(binary)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { DirectoryPath, TokenFile, ApprovalTokenFile, "regtest" })
            start.ArgumentList.Add(argument);
        _process = Process.Start(start) ?? throw new InvalidOperationException("VLS gateway did not start.");
        _stderr = _process.StandardError.ReadToEndAsync();
        _stdout = _process.StandardOutput.ReadToEndAsync();
        await _process.StandardInput.BaseStream.WriteAsync(_seed, ct);
        _process.StandardInput.Close();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (!File.Exists(SocketPath))
        {
            if (_process.HasExited)
                throw new InvalidOperationException("VLS gateway exited before readiness: " + await _stderr);
            await Task.Delay(25, deadline.Token);
        }
        var identity = await ExchangeAsync(new { op = "identity" }, ct: deadline.Token);
        if (!identity.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException("VLS identity refused: " + identity);
    }

    public async Task<JsonElement> ExchangeAsync(object command, string? id = null, string? token = null,
                                                 bool approval = false, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(approval ? ApprovalSocketPath : SocketPath), deadline.Token);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        var request = JsonSerializer.SerializeToUtf8Bytes(new
        {
            token = token ?? (approval ? ApprovalToken : NodeToken),
            id = id ?? Guid.NewGuid().ToString("N"),
            command
        });
        await stream.WriteAsync(request, deadline.Token);
        await stream.WriteAsync(new byte[] { (byte)'\n' }, deadline.Token);
        await stream.FlushAsync(deadline.Token);
        using var reply = new MemoryStream();
        var one = new byte[1];
        while (await stream.ReadAsync(one, deadline.Token) != 0)
        {
            if (one[0] == (byte)'\n')
                break;
            if (reply.Length >= 1024 * 1024)
                throw new IOException("VLS reply exceeds test frame limit.");
            reply.WriteByte(one[0]);
        }
        using var parsed = JsonDocument.Parse(reply.ToArray());
        return parsed.RootElement.Clone();
    }

    public async Task StopAsync()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        if (_stdout is not null)
            await _stdout;
        if (_stderr is not null)
            await _stderr;
        _process?.Dispose();
        _process = null;
        File.Delete(SocketPath);
        File.Delete(ApprovalSocketPath);
    }

    public async Task RestartAsync(CancellationToken ct = default)
    {
        await StopAsync();
        await StartAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        Array.Clear(_seed);
        if (Directory.Exists(DirectoryPath))
            Directory.Delete(DirectoryPath, recursive: true);
    }
}