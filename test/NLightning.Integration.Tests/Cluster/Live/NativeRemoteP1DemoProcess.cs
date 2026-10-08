using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NLightning.Client.Ipc;
using NLightning.Daemon.Interfaces;
using NLightning.Signer;
using NLightning.Testing.Cluster.Topology;

namespace NLightning.Integration.Tests.Cluster.Live;

/// <summary>Runs the shipped demo launcher; it does not duplicate daemon or signer composition.</summary>
internal sealed class NativeRemoteP1DemoProcess : IAsyncDisposable
{
    private readonly string _script;
    private readonly Action<string> _log;
    private readonly string _directory;
    private readonly Dictionary<string, Instance> _instances = new(StringComparer.Ordinal);
    public string Root { get; }

    private NativeRemoteP1DemoProcess(string script, Action<string> log)
    {
        _script = script;
        _log = log;
        _directory = Path.Combine(Path.GetTempPath(), "native-p1-" + Guid.NewGuid().ToString("N"));
        Root = Path.Combine(_directory, "demo");
        Directory.CreateDirectory(_directory);
        PrivateDirectory(_directory);
    }

    public static async Task<NativeRemoteP1DemoProcess> CreateAsync(ITopologyChain chain, Action<string> log,
                                                                  CancellationToken ct)
    {
        var script = Environment.GetEnvironmentVariable("NLTG_NATIVE_DEMO_SCRIPT")
                     ?? Path.Combine(AppContext.BaseDirectory, "native-remote-demo", "demo.py");
        if (!File.Exists(script)) throw new FileNotFoundException("Package the product demo.py in the runner image.", script);
        var demo = new NativeRemoteP1DemoProcess(script, log);
        try
        {
            var core = Path.Combine(demo._directory, "core.json");
            await File.WriteAllTextAsync(core, JsonSerializer.Serialize(new
            {
                RpcEndpoint = $"http://{chain.RpcHost}:{chain.RpcPort}", chain.RpcUser, chain.RpcPassword
            }), ct);
            PrivateFile(core);
            foreach (var (name, seed) in new[] { ("a", new string('a', 64)), ("b", new string('b', 64)) })
            {
                var file = demo.SeedPath(name);
                await File.WriteAllTextAsync(file, seed, ct);
                PrivateFile(file);
            }
            await demo.CommandAsync([
                "init", "--root", demo.Root, "--core-config", core,
                "--dotnet", DotnetPath(), "--signer-dll", typeof(SignerAssemblyMarker).Assembly.Location,
                "--node-dll", typeof(IClientCommandHandler<,>).Assembly.Location,
                "--client-dll", typeof(NamedPipeIpcClient).Assembly.Location,
                "--base-peer-port", ReservePortPair().ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--legacy-channels"
            ], ct);
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(demo.Root, "demo.json"), ct));
            foreach (var node in manifest.RootElement.GetProperty("nodes").EnumerateArray())
            {
                var name = node.GetProperty("name").GetString()!;
                demo._instances.Add(name, new Instance(node.Clone()));
            }
            Assert.Equal(2, demo._instances.Count);
            return demo;
        }
        catch
        {
            await demo.DisposeAsync();
            throw;
        }
    }

    public string PathFor(string name, string property) => _instances[name].Manifest.GetProperty(property).GetString()!;
    public NamedPipeIpcClient Client(string name) => new(PathFor(name, "nodeIpc"), PathFor(name, "nodeCookie"));

    public async Task StartAsync(string name, CancellationToken ct)
    {
        var instance = _instances[name];
        if (instance.Process is not null) throw new InvalidOperationException("Stop the existing launcher before restart.");
        var start = StartInfo(["run", "--root", Root, "--node", name,
            "--seed-a-file", SeedPath("a"), "--seed-b-file", SeedPath("b")]);
        instance.Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        instance.Process = Process.Start(start) ?? throw new InvalidOperationException("Could not start demo launcher.");
        instance.Output = DrainAsync(instance.Process.StandardOutput, instance);
        instance.Error = DrainAsync(instance.Process.StandardError, instance);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var exit = instance.Process.WaitForExitAsync(timeout.Token);
        var completed = await Task.WhenAny(instance.Ready.Task, exit);
        if (completed == exit)
        {
            await exit;
            throw new InvalidOperationException($"Demo {name} exited before readiness: {instance.Process.ExitCode}.");
        }
        await instance.Ready.Task.WaitAsync(timeout.Token);
    }

    public async Task StopAsync(string name)
    {
        var instance = _instances[name];
        if (instance.Process is not { } process) return;
        if (!process.HasExited)
        {
            await SignalAsync(process.Id, "TERM");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
        await Task.WhenAll(instance.Output!, instance.Error!);
        process.Dispose();
        instance.Process = null;
    }

    public Task ClientCommandAsync(string name, string command, CancellationToken ct) =>
        CommandAsync(["client", "--root", Root, "--node", name, "--", command], ct);

    public Task InterruptSignerAsync(string name, CancellationToken ct) =>
        CommandAsync(["stop-signer", "--root", Root, "--node", name], ct);

    private async Task CommandAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        using var process = Process.Start(StartInfo(arguments)) ?? throw new InvalidOperationException("Could not run demo command.");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        var stdout = await output;
        var stderr = await error;
        _log(stdout);
        Assert.True(process.ExitCode == 0, $"Demo command {arguments[0]} failed ({process.ExitCode}): {stderr}");
    }

    private ProcessStartInfo StartInfo(IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("NLTG_PYTHON") ?? "python3")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add(_script);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["PYTHONUNBUFFERED"] = "1";
        return info;
    }

    private async Task DrainAsync(StreamReader reader, Instance instance)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            _log(line);
            if (line.StartsWith("DEMO_NODE_READY ", StringComparison.Ordinal)) instance.Ready.TrySetResult();
        }
    }

    private static async Task SignalAsync(int pid, string signal)
    {
        using var process = Process.Start(new ProcessStartInfo("/bin/kill")
        { ArgumentList = { "-" + signal, pid.ToString(System.Globalization.CultureInfo.InvariantCulture) } });
        if (process is not null) await process.WaitForExitAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var name in _instances.Keys.Reverse()) await StopAsync(name);
        if (Environment.GetEnvironmentVariable("NLTG_KEEP_NATIVE_DEMO") == "1")
            _log($"Native demo artifacts retained at {_directory}; contains private provisioning material.");
        else if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private string SeedPath(string name) => Path.Combine(_directory, "seed-" + name);
    private static string DotnetPath() => Path.GetFullPath(Path.Combine(
        System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet"));

    private static int ReservePortPair()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var first = new TcpListener(IPAddress.Loopback, 0);
            first.Start();
            var port = ((IPEndPoint)first.LocalEndpoint).Port;
            if (port == ushort.MaxValue) continue;
            using var second = new TcpListener(IPAddress.Loopback, port + 1);
            try { second.Start(); return port; }
            catch (SocketException) { }
        }
        throw new InvalidOperationException("Could not reserve two adjacent demo peer ports.");
    }

    private static void PrivateFile(string file)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void PrivateDirectory(string directory)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private sealed class Instance(JsonElement manifest)
    {
        public JsonElement Manifest { get; } = manifest;
        public Process? Process { get; set; }
        public Task? Output { get; set; }
        public Task? Error { get; set; }
        public TaskCompletionSource Ready { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}