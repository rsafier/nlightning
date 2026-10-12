using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NLightning.Domain.Protocol.Constants;
using NLightning.Infrastructure.Bitcoin.Builders;
using NLightning.Infrastructure.Bitcoin.Managers;
using NLightning.Infrastructure.Bitcoin.Services;
using NLightning.Infrastructure.Bitcoin.Signers;
using NLightning.Infrastructure.RemoteSigning;
using NLightning.Infrastructure.Repositories.Memory;
using NLightning.Signer;

namespace NLightning.RemoteSigning.Tests;

/// <summary>The signer runs in another process with an encrypted key file, over a real HTTP/2 Unix socket.</summary>
public sealed class SignerDaemonFixture : IAsyncLifetime
{
    private readonly bool _injected;
    private string? _seed;
    public SignerDaemonFixture() { }
    internal SignerDaemonFixture(bool injected) => _injected = injected;
    internal SignerDaemonFixture(string injectedSeed) { _injected = true; _seed = injectedSeed; }
    public const string Token = "remote-signing-tests-auth-token-000000000000000000";
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "rs-" + Guid.NewGuid().ToString("N"));
    public string SocketPath => Path.Combine(DirectoryPath, "signer.sock");
    public SecureKeyManager LocalKeys { get; private set; } = null!;
    public LocalLightningSigner LocalSigner { get; private set; } = null!;
    private Process? _process;
    private Task<string>? _stderr;
    private ProcessStartInfo? _start;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(DirectoryPath);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var keyFile = Path.Combine(DirectoryPath, "node.key");
        _seed ??= new string('0', 63) + "1";
        LocalKeys = _injected ? SecureKeyManager.FromSeed(Convert.FromHexString(_seed), NetworkConstants.Regtest,
                                                        _ => { }) : new SecureKeyManager(Convert.FromHexString(new string('0', 63) + "1"), NetworkConstants.Regtest,
                                        keyFile, 0);
        if (!_injected)
            LocalKeys.SaveToFile("test-password");
        LocalSigner = new LocalLightningSigner(new FundingOutputBuilder(), new KeyDerivationService(),
                                              NullLogger<LocalLightningSigner>.Instance,
                                              new NLightning.Domain.Node.Options.NodeOptions
                                              { BitcoinNetwork = NetworkConstants.Regtest }, LocalKeys,
                                              new UtxoMemoryRepository());
        var passwordFile = _injected ? "" : WriteSecret("password", "test-password");
        var tokenFile = WriteSecret("token", Token);
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        var arguments = _injected
            ? new[] { typeof(SignerAssemblyMarker).Assembly.Location, "--socket", SocketPath,
                      "--seed-stdin", "--state-file", Path.Combine(DirectoryPath, "state"),
                      "--auth-token-file", tokenFile, "--network", "regtest" }
            : new[] { typeof(SignerAssemblyMarker).Assembly.Location, "--socket", SocketPath,
                      "--key-file", keyFile, "--password-file", passwordFile,
                      "--auth-token-file", tokenFile, "--network", "regtest" };
        start.RedirectStandardInput = _injected;
        foreach (var arg in arguments)
            start.ArgumentList.Add(arg);
        _start = start;
        await StartAsync();
    }

    private async Task StartAsync()
    {
        _process = Process.Start(_start!) ?? throw new InvalidOperationException("Failed to start signer.");
        _stderr = _process.StandardError.ReadToEndAsync();
        if (_injected)
        {
            await _process.StandardInput.WriteLineAsync(_seed);
            _process.StandardInput.Close();
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (await _process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.StartsWith("SIGNER_READY", StringComparison.Ordinal))
                return;
        }
        throw new InvalidOperationException("Signer exited before readiness: " + await _stderr);
    }

    public RemoteSignerOptions Options(string? network = null, string? token = null) => new()
    {
        SocketPath = SocketPath,
        Network = network ?? "regtest",
        AuthToken = token ?? Token,
        TimeoutSeconds = 15
    };

    private string WriteSecret(string name, string value)
    {
        var path = Path.Combine(DirectoryPath, name);
        File.WriteAllText(path, value);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    public async Task StopAsync()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        _process?.Dispose();
        _process = null;
        File.Delete(SocketPath);
    }

    public void ReplaceInjectedSeed(string hexSeed) => _seed = hexSeed;

    public async Task RestartAsync()
    {
        await StopAsync();
        await StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        _process?.Dispose();
        LocalKeys?.Dispose();
        Directory.Delete(DirectoryPath, recursive: true);
    }
}