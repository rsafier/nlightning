using System.Diagnostics;
using MessagePack;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace NLightning.Daemon.Hosting;

using Client.Ipc;
using Contracts.Utilities;
using Transport.Ipc.MessagePack;

/// <summary>Runs independently configured daemon processes against their enrolled remote native signers.</summary>
public sealed class HostedNativeNodeProcessSupervisor : IAsyncDisposable
{
    private readonly HostedNodeIsolationManifest _manifest;
    private readonly Dictionary<string, HostedNativeNodeProcess> _nodes = new(StringComparer.Ordinal);
    private bool _disposed;

    public HostedNativeNodeProcessSupervisor(HostedNodeIsolationManifest manifest)
    {
        _manifest = manifest;
        _manifest.Validate();
        MessagePackSerializer.DefaultOptions = NLightningMessagePackOptions.Options;
    }

    public IReadOnlyCollection<HostedNativeNodeProcess> Nodes => _nodes.Values.ToArray();

    public async Task StartAsync(IEnumerable<HostedDaemonLaunch> launches, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _manifest.Validate();
        var specifications = launches.Select(launch => launch with { Enrollment = _manifest.Resolve(launch.Enrollment) }).ToArray();
        if (_nodes.Count != 0 || specifications.Length != _manifest.Nodes.Count)
            throw new InvalidOperationException("Start requires exactly one launch specification for every enrolled node.");
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var specification in specifications)
        {
            if (!nodeIds.Add(specification.Enrollment.Signer.Context.NodeId))
                throw new ArgumentException("A launch does not belong to this hosted enrollment.");
            ValidateConfiguration(specification);
        }
        HostedNodeIsolationManifest.ValidateCredentialFiles(_manifest.Nodes.Select(node => node.Signer.CredentialPath));
        try
        {
            foreach (var specification in specifications)
            {
                var node = new HostedNativeNodeProcess(specification);
                _nodes.Add(node.Context.NodeId, node);
                await node.StartAsync(cancellationToken);
            }
            _manifest.ValidateProvisionedCredentials();
        }
        catch
        {
            foreach (var node in _nodes.Values.Reverse()) await node.StopAsync(CancellationToken.None);
            _nodes.Clear();
            throw;
        }
    }

    public HostedNativeNodeProcess Node(string nodeId) => _nodes.TryGetValue(nodeId, out var node)
        ? node : throw new KeyNotFoundException("The node is not enrolled in this supervisor.");

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var node in _nodes.Values.Reverse()) await node.StopAsync(CancellationToken.None);
        _nodes.Clear();
    }

    internal static void ValidateConfiguration(HostedDaemonLaunch launch)
    {
        var enrollment = launch.Enrollment;
        var context = enrollment.Signer.Context;
        if (!Path.IsPathFullyQualified(launch.ExecutablePath)
         || !Path.IsPathFullyQualified(launch.ConfigurationFilePath)
         || launch.ManagedAssemblyPath is { } assembly && !Path.IsPathFullyQualified(assembly))
            throw new ArgumentException("Hosted launch paths must be absolute.");
        var directory = Path.GetDirectoryName(launch.ConfigurationFilePath)!;
        if (Path.GetFullPath(NodeUtils.GetCookieFilePath(directory)) != Path.GetFullPath(enrollment.NodeCredentialPath)
         || Path.GetFullPath(NodeUtils.GetNamedPipeFilePath(directory)) != Path.GetFullPath(enrollment.NodeIpcPath))
            throw new ArgumentException("The daemon configuration directory does not own its enrolled IPC resources.");
        var configuration = new ConfigurationBuilder().AddJsonFile(launch.ConfigurationFilePath, false).Build();
        if (!string.Equals(configuration["Signing:Mode"], "RemoteNative", StringComparison.OrdinalIgnoreCase)
         || configuration["Signing:NodeId"] != context.NodeId || configuration["Signing:OwnerId"] != context.OwnerId
         || configuration["Signing:SignerId"] != context.SignerId
         || !string.Equals(configuration["Signing:ExpectedNodePublicKey"], context.NodePublicKey.ToString(), StringComparison.OrdinalIgnoreCase)
         || !string.Equals(configuration["Node:Network"], context.Network, StringComparison.OrdinalIgnoreCase)
         || Path.GetFullPath(configuration["Signing:SocketPath"] ?? "") != Path.GetFullPath(enrollment.Signer.SocketPath)
         || Path.GetFullPath(configuration["Signing:AuthTokenFile"] ?? "") != Path.GetFullPath(enrollment.Signer.CredentialPath))
            throw new ArgumentException("The daemon configuration does not match its immutable signing enrollment.");
        if (!string.Equals(configuration["Database:Provider"], "Sqlite", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("This hosted process configuration requires a separate SQLite database per node.");
        var database = new SqliteConnectionStringBuilder(configuration["Database:ConnectionString"] ?? "").DataSource;
        if (!Path.IsPathFullyQualified(database) || Path.GetFullPath(database) != Path.GetFullPath(enrollment.PrivateDatabasePath))
            throw new ArgumentException("The daemon database does not match its enrolled private database.");
        var listeners = configuration.GetSection("Node:ListenAddresses").Get<string[]>() ?? [];
        var expected = enrollment.PeerListeners.Select(listener => new System.Net.IPEndPoint(
            System.Net.IPAddress.Parse(listener.Address), listener.Port).ToString()).Order(StringComparer.Ordinal).ToArray();
        var actual = listeners.Select(listener => System.Net.IPEndPoint.Parse(listener).ToString())
            .Order(StringComparer.Ordinal).ToArray();
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
            throw new ArgumentException("The daemon listeners do not match its enrolled Lightning endpoints.");
        if (configuration.GetValue<bool>("Daemon"))
            throw new ArgumentException("Hosted daemons must run in the foreground under their supervisor.");
    }
}

public sealed record HostedDaemonLaunch(HostedNativeNodeEnrollment Enrollment, string ConfigurationFilePath,
                                       string ExecutablePath, string? ManagedAssemblyPath = null);

public sealed class HostedNativeNodeProcess
{
    private readonly HostedDaemonLaunch _launch;
    private readonly Queue<string> _recentOutput = new();
    private readonly object _outputLock = new();
    private Process? _process;
    private Task? _stdout;
    private Task? _stderr;
    public Domain.Signing.NodeSigningContext Context => _launch.Enrollment.Signer.Context;
    public int? ProcessId => _process is { HasExited: false } process ? process.Id : null;

    internal HostedNativeNodeProcess(HostedDaemonLaunch launch) => _launch = launch;

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false }) throw new InvalidOperationException("The hosted daemon is already running.");
        HostedNativeNodeProcessSupervisor.ValidateConfiguration(_launch);
        _ = new HostedNodeIsolationManifest([_launch.Enrollment]);
        var start = CreateStartInfo();
        _process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch the hosted daemon.");
        try
        {
            _process.StandardInput.Close();
            _stdout = DrainAsync(_process.StandardOutput);
            _stderr = DrainAsync(_process.StandardError);
            using var ready = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ready.CancelAfter(TimeSpan.FromMinutes(2));
            while (true)
            {
                ready.Token.ThrowIfCancellationRequested();
                if (_process.HasExited) throw new InvalidOperationException("The hosted daemon exited before authenticated readiness. " + RecentOutput);
                try
                {
                    await using var client = CreateClient();
                    var info = await client.GetNodeInfoAsync(ready.Token);
                    if (info.NodeId != Context.NodeId || info.OwnerId != Context.OwnerId || info.SignerId != Context.SignerId
                     || info.PubKey != Context.NodePublicKey || info.Network.Name != Context.Network)
                        throw new InvalidOperationException("Hosted daemon reported another enrollment.");
                    return;
                }
                catch (Exception error) when (error is IOException or TimeoutException
                    || error is InvalidOperationException && error.Message.StartsWith("IPC error auth_failed:", StringComparison.Ordinal))
                {
                    await Task.Delay(100, ready.Token);
                }
            }
        }
        catch
        {
            await StopAsync(CancellationToken.None);
            throw;
        }
    }

    internal ProcessStartInfo CreateStartInfo()
    {
        var start = new ProcessStartInfo(_launch.ExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(_launch.ConfigurationFilePath)!
        };
        foreach (var variable in start.Environment.Keys.Where(key => key.StartsWith("NLTG_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(variable);
        if (_launch.ManagedAssemblyPath is { } assembly) start.ArgumentList.Add(assembly);
        foreach (var argument in new[] { "--config", _launch.ConfigurationFilePath, "--network", Context.Network, "--daemon=false" })
            start.ArgumentList.Add(argument);
        return start;
    }

    public NamedPipeIpcClient CreateClient() => new(_launch.Enrollment.NodeIpcPath,
                                                  _launch.Enrollment.NodeCredentialPath);

    public string RecentOutput
    {
        get { lock (_outputLock) return string.Join(Environment.NewLine, _recentOutput); }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken);
        await StartAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_process is null) return;
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(cancellationToken);
        }
        if (_stdout is not null) await _stdout;
        if (_stderr is not null) await _stderr;
        _process.Dispose();
        _process = null;
    }

    private async Task DrainAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            lock (_outputLock)
            {
                _recentOutput.Enqueue(line.Length > 4096 ? line[..4096] : line);
                while (_recentOutput.Count > 100) _recentOutput.Dequeue();
            }
        }
    }
}