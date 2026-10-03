using System.Globalization;
using Docker.DotNet;
using Docker.DotNet.Models;
using NBitcoin.RPC;
using NLightning.Tests.Utils;

namespace NLightning.Integration.Tests.Fixtures.Cln;

using Docker.Utils;

/// <summary>
/// The Docker backend of <see cref="ClnFixture"/> (the default, unchanged from the fixture before the cluster port): its
/// own bitcoind (<see cref="ClnFixture.BitcoinContainerName"/>) and CLN (<see cref="ClnFixture.ClnContainerName"/>) in
/// its own Docker network (<see cref="ClnFixture.NetworkName"/>), every port the tests use published on
/// <c>127.0.0.1</c>, and the peers dialling us at <see cref="ClnFixture.HostAddressFromContainers"/>.
/// </summary>
/// <remarks>
/// bitcoind publishes RPC and the ZMQ raw block/tx feeds on <c>127.0.0.1</c> for the in-process NLightning nodes; CLN
/// (the official <c>elementsproject/lightningd</c> image) reaches bitcoind by name on the network and publishes its p2p
/// port on <c>127.0.0.1</c>. The containers and the network are force-removed before start and on dispose.
/// </remarks>
public sealed class DockerClnBackend : IClnBackend
{
    private const string BitcoinImage = "polarlightning/bitcoind";
    private const string BitcoinTag = "29.0";
    private const int ZmqBlockPort = 28334;
    private const int ZmqTxPort = 28335;

    private static readonly TimeSpan s_readyTimeout = TimeSpan.FromMinutes(2);

    private readonly DockerClient _client = new DockerClientConfiguration().CreateClient();
    private readonly List<DockerExtraClnNode> _extraNodes = [];

    private RegtestBitcoinEndpoint? _bitcoin;
    private ClnClient? _cln;

    public TestBackendKind Kind => TestBackendKind.Docker;

    public RegtestBitcoinEndpoint Bitcoin =>
        _bitcoin ?? throw new InvalidOperationException("The CLN fixture is not running");

    public ClnClient Cln => _cln ?? throw new InvalidOperationException("The CLN fixture is not running");

    public string ClnNodeId { get; private set; } = string.Empty;

    public string ClnHost => "127.0.0.1";

    public int ClnPort { get; private set; }

    public string HostAddressForPeers => ClnFixture.HostAddressFromContainers;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await DockerContainerUtils.EnsureImageAsync(_client, BitcoinImage, BitcoinTag);
        await DockerContainerUtils.EnsureImageAsync(_client, ClnFixture.ClnImage, ClnFixture.ClnTag);

        await DockerContainerUtils.RemoveContainerAsync(_client, ClnFixture.ClnContainerName);
        await DockerContainerUtils.RemoveContainerAsync(_client, ClnFixture.BitcoinContainerName);
        await RemoveNetworkAsync();
        await _client.Networks.CreateNetworkAsync(new NetworksCreateParameters
        {
            Name = ClnFixture.NetworkName,
            Driver = "bridge"
        }, cancellationToken);

        // bitcoind
        var bitcoinPorts = await StartContainerAsync($"{BitcoinImage}:{BitcoinTag}", ClnFixture.BitcoinContainerName,
                                                     [],
                                                     [
                                                         "bitcoind", "-regtest", "-server=1",
                                                         $"-rpcuser={ClnFixture.RpcUser}",
                                                         $"-rpcpassword={ClnFixture.RpcPassword}",
                                                         "-rpcbind=0.0.0.0", "-rpcallowip=0.0.0.0/0",
                                                         $"-rpcport={ClnFixture.RpcPort}", "-rpcworkqueue=1024",
                                                         $"-zmqpubrawblock=tcp://0.0.0.0:{ZmqBlockPort}",
                                                         $"-zmqpubrawtx=tcp://0.0.0.0:{ZmqTxPort}",
                                                         "-txindex=1", "-fallbackfee=0.0002", "-dnsseed=0",
                                                         "-listen=0", "-printtoconsole"
                                                     ], [ClnFixture.RpcPort, ZmqBlockPort, ZmqTxPort]);
        var rpc = new RPCClient($"{ClnFixture.RpcUser}:{ClnFixture.RpcPassword}",
                                $"http://127.0.0.1:{bitcoinPorts[ClnFixture.RpcPort]}", NBitcoin.Network.RegTest);
        await DockerContainerUtils.WaitUntilReadyAsync(ClnFixture.BitcoinContainerName,
                                                       async ct => await rpc.GetBlockCountAsync(ct), s_readyTimeout);
        await rpc.SendCommandAsync("createwallet", "miner");
        _bitcoin = new RegtestBitcoinEndpoint(rpc, "127.0.0.1", bitcoinPorts[ZmqBlockPort], bitcoinPorts[ZmqTxPort]);
        await rpc.GenerateToAddressAsync(101, await rpc.GetNewAddressAsync(cancellationToken), cancellationToken);

        // CLN
        var clnPorts = await StartContainerAsync($"{ClnFixture.ClnImage}:{ClnFixture.ClnTag}",
                                                 ClnFixture.ClnContainerName, ["LIGHTNINGD_NETWORK=regtest"],
                                                 BuildClnArgs(ClnFixture.ClnContainerName, enforceFeeLimits: true, []),
                                                 [ClnFixture.ClnP2PPort]);
        ClnPort = clnPorts[ClnFixture.ClnP2PPort];
        var cln = new ClnClient(_client, ClnFixture.ClnContainerName);
        await DockerContainerUtils.WaitUntilReadyAsync(ClnFixture.ClnContainerName,
                                                       async ct => await cln.GetInfoAsync(ct), s_readyTimeout);
        _cln = cln;
        ClnNodeId = (await cln.GetInfoAsync(cancellationToken))["id"]!.GetValue<string>();
    }

    public async Task<ExtraClnNode> StartClnAsync(ClnNodeSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        await DockerContainerUtils.RemoveContainerAsync(_client, spec.Name);
        var fixedPort = spec.Restartable ? await PortPoolUtil.GetAvailablePortAsync() : 0;
        try
        {
            var portKey = $"{ClnFixture.ClnP2PPort}/tcp";
            var container = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = $"{ClnFixture.ClnImage}:{ClnFixture.ClnTag}",
                Name = spec.Name,
                Hostname = spec.Name,
                Env = ["LIGHTNINGD_NETWORK=regtest"],
                Cmd = BuildClnArgs(spec.Name, spec.EnforceFeeLimits, spec.ExtraArgs),
                ExposedPorts = spec.ReachableFromTests
                                   ? new Dictionary<string, EmptyStruct> { [portKey] = default }
                                   : null,
                HostConfig = new HostConfig
                {
                    NetworkMode = ClnFixture.NetworkName,
                    PortBindings = spec.ReachableFromTests
                                       ? new Dictionary<string, IList<PortBinding>>
                                       {
                                           [portKey] =
                                           [
                                               new PortBinding
                                               {
                                                   HostIP = "127.0.0.1",
                                                   HostPort = fixedPort == 0
                                                                  ? string.Empty
                                                                  : fixedPort.ToString(CultureInfo.InvariantCulture)
                                               }
                                           ]
                                       }
                                       : null,
                    ExtraHosts = OperatingSystem.IsLinux()
                                     ? [$"{ClnFixture.HostAddressFromContainers}:host-gateway"]
                                     : null
                }
            }, cancellationToken) ?? throw new InvalidOperationException($"Failed to create {spec.Name}");
            await _client.Containers.StartContainerAsync(container.ID, new ContainerStartParameters(),
                                                         cancellationToken);
            var hostPort = spec.ReachableFromTests
                               ? (await WaitForPortsAsync(container.ID, spec.Name, [ClnFixture.ClnP2PPort]))
                                     [ClnFixture.ClnP2PPort]
                               : 0;
            var client = new ClnClient(_client, spec.Name);
            await DockerContainerUtils.WaitUntilReadyAsync(spec.Name, async ct => await client.GetInfoAsync(ct),
                                                           s_readyTimeout);
            var nodeId = (await client.GetInfoAsync(cancellationToken))["id"]!.GetValue<string>();
            var node = new DockerExtraClnNode(this, spec, client, nodeId, hostPort, fixedPort);
            lock (_extraNodes)
                _extraNodes.Add(node);
            return node;
        }
        catch
        {
            await DockerContainerUtils.RemoveContainerAsync(_client, spec.Name);
            if (fixedPort != 0)
                PortPoolUtil.ReleasePort(fixedPort);
            throw;
        }
    }

    public Task DumpClnLogAsync(int tail) =>
        DockerDiagnostics.DumpContainerLogsAsync([ClnFixture.ClnContainerName], tail);

    public async ValueTask DisposeAsync()
    {
        List<DockerExtraClnNode> extraNodes;
        lock (_extraNodes)
            extraNodes = [.. _extraNodes];
        foreach (var node in extraNodes)
            await node.DisposeAsync();

        await DockerContainerUtils.RemoveContainerAsync(_client, ClnFixture.ClnContainerName);
        await DockerContainerUtils.RemoveContainerAsync(_client, ClnFixture.BitcoinContainerName);
        await RemoveNetworkAsync();
        _client.Dispose();
    }

    /// <summary>
    /// CLN's command line: bitcoind by its container name, the p2p port on every interface, the name as alias, debug
    /// logs, <c>--developer --dev-bitcoind-poll=1</c> (CLN sees a new block within a second, the default poll is 30 s),
    /// then <c>--ignore-fee-limits=false</c> (on testnet/regtest CLN ignores its feerate limits by default, which would
    /// make any feerate we send look fine) and <paramref name="extraArgs"/>.
    /// </summary>
    public static List<string> BuildClnArgs(string name, bool enforceFeeLimits, IEnumerable<string> extraArgs)
    {
        List<string> args =
        [
            $"--bitcoin-rpcconnect={ClnFixture.BitcoinContainerName}",
            $"--bitcoin-rpcport={ClnFixture.RpcPort}",
            $"--bitcoin-rpcuser={ClnFixture.RpcUser}",
            $"--bitcoin-rpcpassword={ClnFixture.RpcPassword}",
            $"--bind-addr=0.0.0.0:{ClnFixture.ClnP2PPort}",
            $"--alias={name}",
            "--log-level=debug",
            "--developer",
            "--dev-bitcoind-poll=1"
        ];
        if (enforceFeeLimits)
            args.Add("--ignore-fee-limits=false");
        args.AddRange(extraArgs);
        return args;
    }

    private async Task RemoveExtraAsync(DockerExtraClnNode node)
    {
        lock (_extraNodes)
            _extraNodes.Remove(node);
        await DockerContainerUtils.RemoveContainerAsync(_client, node.Name);
    }

    /// <summary>
    /// Creates and starts a container on <see cref="ClnFixture.NetworkName"/> with <paramref name="containerPorts"/>
    /// published on free <c>127.0.0.1</c> ports.
    /// </summary>
    /// <returns>The host port of each container port.</returns>
    private async Task<Dictionary<int, int>> StartContainerAsync(string image, string name, IList<string> env,
                                                                 IList<string> cmd, IReadOnlyList<int> containerPorts)
    {
        var parameters = new CreateContainerParameters
        {
            Image = image,
            Name = name,
            Hostname = name,
            Env = env,
            Cmd = cmd,
            ExposedPorts = containerPorts.ToDictionary(p => $"{p}/tcp", _ => default(EmptyStruct)),
            HostConfig = new HostConfig
            {
                NetworkMode = ClnFixture.NetworkName,
                PortBindings = containerPorts.ToDictionary(
                    p => $"{p}/tcp",
                    IList<PortBinding> (_) => [new PortBinding { HostIP = "127.0.0.1", HostPort = string.Empty }]),
                // OrbStack and Docker Desktop resolve host.docker.internal themselves; plain Linux Docker needs the alias
                ExtraHosts = OperatingSystem.IsLinux() ? [$"{ClnFixture.HostAddressFromContainers}:host-gateway"] : null
            }
        };

        var container = await _client.Containers.CreateContainerAsync(parameters)
                     ?? throw new InvalidOperationException($"Failed to create the {name} container");
        await _client.Containers.StartContainerAsync(container.ID, new ContainerStartParameters());
        return await WaitForPortsAsync(container.ID, name, containerPorts);
    }

    private async Task<Dictionary<int, int>> WaitForPortsAsync(string containerId, string name,
                                                               IReadOnlyList<int> containerPorts)
    {
        var deadline = DateTime.UtcNow.AddMinutes(1);
        while (true)
        {
            var inspect = await _client.Containers.InspectContainerAsync(containerId);
            var hostPorts = new Dictionary<int, int>();
            foreach (var port in containerPorts)
            {
                if (inspect.NetworkSettings?.Ports is { } ports
                 && ports.TryGetValue($"{port}/tcp", out var bindings)
                 && bindings is { Count: > 0 }
                 && int.TryParse(bindings[0].HostPort, out var hostPort)
                 && hostPort > 0)
                    hostPorts[port] = hostPort;
            }

            if (hostPorts.Count == containerPorts.Count)
                return hostPorts;

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Docker did not publish the ports of {name}");

            await Task.Delay(100);
        }
    }

    private async Task RemoveNetworkAsync()
    {
        try
        {
            await _client.Networks.DeleteNetworkAsync(ClnFixture.NetworkName);
        }
        catch
        {
            // ignored: not there
        }
    }

    private sealed class DockerExtraClnNode(DockerClnBackend backend, ClnNodeSpec spec, ClnClient client,
                                            string nodeId, int hostPort, int fixedPort)
        : ExtraClnNode(spec, client, nodeId, "127.0.0.1", hostPort)
    {
        private int _disposed;

        public override async Task RestartAsync(CancellationToken cancellationToken)
        {
            if (!Spec.Restartable)
                throw new InvalidOperationException($"{Name} was not started restartable (its host port would change)");

            await backend._client.Containers.RestartContainerAsync(
                Name, new ContainerRestartParameters { WaitBeforeKillSeconds = 10 }, cancellationToken);
            await DockerContainerUtils.WaitUntilReadyAsync(Name, async c => await Client.GetInfoAsync(c),
                                                           s_readyTimeout);
        }

        public override Task DumpLogAsync(int tail) => DockerDiagnostics.DumpContainerLogsAsync([Name], tail);

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            await backend.RemoveExtraAsync(this);
            if (fixedPort != 0)
                PortPoolUtil.ReleasePort(fixedPort);
        }
    }
}