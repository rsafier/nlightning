using System.Diagnostics;
using System.Security.Cryptography;
using Grpc.Core;
using NLightning.Infrastructure.RemoteSigning;
using NLightning.Signer;

namespace NLightning.RemoteSigning.Tests;

/// <summary>Owns independently provisioned signer processes for hosted-node acceptance.</summary>
public sealed class HostedNativeSignerSupervisor : IAsyncDisposable
{
    private readonly List<HostedSigner> _signers = [];

    public async Task<HostedSigner> AddAsync(string name, string seed)
    {
        if (_signers.Any(signer => signer.Name == name))
            throw new ArgumentException("Hosted instance names must be unique.", nameof(name));
        var signer = new HostedSigner(name, seed);
        _signers.Add(signer);
        await signer.StartAsync();
        return signer;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var signer in _signers.AsEnumerable().Reverse())
            await signer.DisposeAsync();
    }

    public sealed class HostedSigner : IAsyncDisposable
    {
        private readonly string _seed;
        private Process? _process;
        private Task? _stdout;
        private Task? _stderr;
        private readonly Queue<string> _recentOutput = new();
        private readonly object _outputLock = new();
        private string? _expectedNodePublicKey;
        public string Name { get; }
        public string DirectoryPath { get; }
        public string SocketPath => Path.Combine(DirectoryPath, "signer.sock");
        public string StatePath => Path.Combine(DirectoryPath, "state");
        public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        internal HostedSigner(string name, string seed)
        {
            Name = name;
            _seed = seed;
            DirectoryPath = Path.Combine(Path.GetTempPath(), "hosted-native-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var tokenPath = Path.Combine(DirectoryPath, "token");
            File.WriteAllText(tokenPath, Token);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        public RemoteSignerOptions Options() => new()
        {
            SocketPath = SocketPath,
            AuthToken = Token,
            Network = "regtest",
            NodeId = "node-" + Name,
            OwnerId = "owner-" + Name,
            SignerId = "signer-" + Name,
            ExpectedNodePublicKey = _expectedNodePublicKey,
            TimeoutSeconds = 2
        };

        internal async Task StartAsync()
        {
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[]
                     {
                         typeof(SignerAssemblyMarker).Assembly.Location, "--socket", SocketPath,
                         "--seed-stdin", "--state-file", StatePath,
                         "--auth-token-file", Path.Combine(DirectoryPath, "token"), "--network", "regtest",
                         "--node-id", "node-" + Name, "--owner-id", "owner-" + Name,
                         "--signer-id", "signer-" + Name
                     })
                start.ArgumentList.Add(argument);
            _process = Process.Start(start) ?? throw new InvalidOperationException("Could not start hosted signer.");
            _stderr = DrainAsync(_process.StandardError);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await _process.StandardInput.WriteLineAsync(_seed);
                _process.StandardInput.Close();
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _stdout = DrainAsync(_process.StandardOutput, ready);
                await ready.Task.WaitAsync(timeout.Token);
                // The listener marker precedes the first HTTP/2 request and lazy RPC activation.
                // Only the read-only identity probe may be retried during bounded startup.
                while (true)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    if (_process.HasExited)
                        throw new InvalidOperationException("Hosted signer exited before authenticated readiness.");
                    try
                    {
                        var identity = await Task.Run(() =>
                        {
                            using var connection = new RemoteSignerConnection(Options());
                            return connection.Identity.NodePublicKey.ToString();
                        }, timeout.Token).WaitAsync(timeout.Token);
                        _expectedNodePublicKey ??= identity;
                        return;
                    }
                    catch (RemoteSignerTransportException error) when (
                        error.Request?.Operation == SignerOperations.Identity
                     && error.InnerException is RpcException { StatusCode: StatusCode.DeadlineExceeded or StatusCode.Unavailable })
                    {
                        await Task.Delay(100, timeout.Token);
                    }
                }
            }
            catch (Exception error)
            {
                await StopAsync();
                string output;
                lock (_outputLock) output = string.Join(Environment.NewLine, _recentOutput);
                throw new InvalidOperationException("Hosted signer failed before authenticated readiness: " + output, error);
            }
        }

        public async Task StopAsync()
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
            if (_stdout is not null) await _stdout;
            if (_stderr is not null) await _stderr;
            _process?.Dispose();
            _process = null;
            File.Delete(SocketPath);
        }

        private async Task DrainAsync(StreamReader reader, TaskCompletionSource? ready = null)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                lock (_outputLock)
                {
                    _recentOutput.Enqueue(line.Length > 4096 ? line[..4096] : line);
                    while (_recentOutput.Count > 100) _recentOutput.Dequeue();
                }
                if (line.StartsWith("SIGNER_READY", StringComparison.Ordinal)) ready?.TrySetResult();
            }
            ready?.TrySetException(new InvalidOperationException("Hosted signer exited before listener readiness."));
        }

        public async Task RestartAsync()
        {
            await StopAsync();
            await StartAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}