using System.Diagnostics;
using System.Security.Cryptography;
using Grpc.Core;

namespace NLightning.RemoteSigning.Tests;

using Infrastructure.RemoteSigning;
using Signer;

/// <summary>Actual executable admission and recovery checks without Bitcoin Core or containers.</summary>
public sealed class NativeP1SignerLifecycleTests
{
    [Fact]
    public async Task Given_InjectedSigner_When_GracefullyRestarted_Then_IdentityAndHistoriesAreRetained()
    {
        // Arrange
        await using var signer = new LifecycleSigner();
        await signer.StartAsync();
        using var connection = new RemoteSignerConnection(signer.Options());
        var identity = connection.Identity.NodePublicKey;
        var index = new RemoteLightningSigner(connection).CreateNewChannel(out _, out _);
        await signer.StopAsync();
        var history = signer.Snapshot();

        // Act
        await signer.StartAsync();
        using var restored = new RemoteSignerConnection(signer.Options());
        Assert.Equal(identity, restored.Identity.NodePublicKey);
        await signer.StopAsync();

        // Assert: identity readiness must not reset or rewrite any durable history.
        signer.AssertSnapshot(history);
        await signer.StartAsync();
        using var allocated = new RemoteSignerConnection(signer.Options());
        Assert.True(new RemoteLightningSigner(allocated).CreateNewChannel(out _, out _) > index);
        Assert.False(File.Exists(Path.Combine(signer.DirectoryPath, "node.key")));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".key-index")]
    [InlineData(".enrollment")]
    [InlineData(".nonces")]
    [InlineData(".swap-sessions")]
    public async Task Given_ExistingSigner_When_RequiredHistoryIsMissing_Then_StartupRefusesWithoutRepair(string suffix)
    {
        // Arrange
        await using var signer = new LifecycleSigner();
        await signer.StartAsync();
        await signer.StopAsync();
        var missing = signer.StatePath + suffix;
        File.Delete(missing);
        var history = signer.Snapshot();

        // Act
        await signer.AssertStartupRefusedAsync();

        // Assert
        Assert.False(File.Exists(missing));
        signer.AssertSnapshot(history);
    }

    [Fact]
    public async Task Given_ExistingSigner_When_AnotherSeedIsInjected_Then_HistoryIsUnchanged()
    {
        // Arrange
        await using var signer = new LifecycleSigner();
        await signer.StartAsync();
        await signer.StopAsync();
        var history = signer.Snapshot();

        // Act
        await signer.AssertStartupRefusedAsync(new string('0', 63) + "2");

        // Assert
        signer.AssertSnapshot(history);
    }

    [Fact]
    public async Task Given_AuthorityMarker_When_PrototypeModeIsRequested_Then_NoDowngradeOccurs()
    {
        // Arrange
        await using var signer = new LifecycleSigner();
        await signer.StartAsync();
        await signer.StopAsync();
        signer.WritePrivate(signer.StatePath + ".authority-profile", new string('a', 64));
        var history = signer.Snapshot();

        // Act
        await signer.AssertStartupRefusedAsync();

        // Assert
        signer.AssertSnapshot(history);
    }

    [Fact]
    public async Task Given_LiveSignerSocket_When_AnotherProcessStarts_Then_ListenerIsNotStolen()
    {
        // Arrange
        await using var signer = new LifecycleSigner();
        await signer.StartAsync();
        using var original = new RemoteSignerConnection(signer.Options());
        var identity = original.Identity.NodePublicKey;

        // Act
        await signer.AssertStartupRefusedAsync();

        // Assert: the original authenticated listener remains available.
        Assert.True(Path.Exists(signer.SocketPath));
        using var retained = new RemoteSignerConnection(signer.Options());
        Assert.Equal(identity, retained.Identity.NodePublicKey);
    }

    [Fact]
    public async Task Given_KilledSigner_When_StaleSocketExists_Then_StartupDoesNotUnlinkIt()
    {
        // Arrange
        await using var signer = new LifecycleSigner();
        await signer.StartAsync();
        await signer.KillAsync();
        Assert.True(Path.Exists(signer.SocketPath));
        var history = signer.Snapshot();

        // Act
        await signer.AssertStartupRefusedAsync();

        // Assert
        Assert.True(Path.Exists(signer.SocketPath));
        signer.AssertSnapshot(history);
    }

    private sealed class LifecycleSigner : IAsyncDisposable
    {
        private const string Seed = "0000000000000000000000000000000000000000000000000000000000000001";
        private const string Token = "native-p1-lifecycle-token-0000000000000000000000";
        private Process? _process;
        private Task<string>? _stdout;
        private Task<string>? _stderr;
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "p1-life-" + Guid.NewGuid().ToString("N"));
        public string StatePath => Path.Combine(DirectoryPath, "state");
        public string SocketPath => Path.Combine(DirectoryPath, "signer.sock");

        public LifecycleSigner()
        {
            Directory.CreateDirectory(DirectoryPath);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            WritePrivate(Path.Combine(DirectoryPath, "token"), Token);
        }

        public void WritePrivate(string path, string text)
        {
            File.WriteAllText(path, text);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        public RemoteSignerOptions Options() => new()
        {
            SocketPath = SocketPath, AuthToken = Token, Network = "regtest", TimeoutSeconds = 2
        };

        private Process Launch()
        {
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("NLTG_", StringComparison.OrdinalIgnoreCase)).ToArray())
                start.Environment.Remove(key);
            foreach (var argument in new[] { typeof(SignerAssemblyMarker).Assembly.Location, "--seed-stdin",
                "--state-file", StatePath, "--socket", SocketPath, "--auth-token-file",
                Path.Combine(DirectoryPath, "token"), "--network", "regtest" })
                start.ArgumentList.Add(argument);
            return Process.Start(start) ?? throw new InvalidOperationException("Could not start signer.");
        }

        public async Task StartAsync()
        {
            Assert.Null(_process);
            _process = Launch();
            _stderr = _process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await _process.StandardInput.WriteLineAsync(Seed.AsMemory(), timeout.Token);
            _process.StandardInput.Close();
            while (await _process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (!line.StartsWith("SIGNER_READY ", StringComparison.Ordinal)) continue;
                _stdout = _process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
                while (true)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    try
                    {
                        using var authenticated = new RemoteSignerConnection(Options());
                        return;
                    }
                    catch (RemoteSignerTransportException error) when (
                        error.Request?.Operation == SignerOperations.Identity
                     && error.InnerException is RpcException { StatusCode: StatusCode.Unavailable or StatusCode.DeadlineExceeded })
                    {
                        await Task.Delay(100, timeout.Token);
                    }
                }
            }
            throw new InvalidOperationException("Signer failed before readiness: " + await _stderr);
        }

        public async Task AssertStartupRefusedAsync(string seed = Seed)
        {
            using var process = Launch();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                try { await process.StandardInput.WriteLineAsync(seed.AsMemory(), timeout.Token); }
                catch (IOException) { }
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
                Assert.NotEqual(0, process.ExitCode);
                var output = await stdout;
                var errors = await stderr;
                Assert.DoesNotContain("SIGNER_READY", output);
                Assert.DoesNotContain(seed, output);
                Assert.DoesNotContain(seed, errors);
                Assert.DoesNotContain(Token, output);
                Assert.DoesNotContain(Token, errors);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
        }

        public Dictionary<string, byte[]> Snapshot() => Directory.GetFiles(DirectoryPath, "state*")
            .Where(path => !path.EndsWith(".lock", StringComparison.Ordinal))
            .ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)), StringComparer.Ordinal);

        public void AssertSnapshot(Dictionary<string, byte[]> expected)
        {
            var current = Snapshot();
            Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), current.Keys.Order(StringComparer.Ordinal));
            foreach (var (path, digest) in expected) Assert.Equal(digest, current[path]);
        }

        public async Task StopAsync()
        {
            if (_process is null) return;
            if (!_process.HasExited)
            {
                var start = new ProcessStartInfo("/bin/kill") { UseShellExecute = false };
                start.ArgumentList.Add("-TERM");
                start.ArgumentList.Add(_process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                using var signal = Process.Start(start)!;
                await signal.WaitForExitAsync(TestContext.Current.CancellationToken);
                Assert.Equal(0, signal.ExitCode);
                await _process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(15));
            }
            if (_stdout is not null) await _stdout;
            if (_stderr is not null) await _stderr;
            _process.Dispose();
            _process = null;
            Assert.False(Path.Exists(SocketPath));
        }

        public async Task KillAsync()
        {
            if (_process is null) return;
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            _process.Dispose();
            _process = null;
        }

        public async ValueTask DisposeAsync()
        {
            await KillAsync();
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}