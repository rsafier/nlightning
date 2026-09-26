using Docker.DotNet;
using Docker.DotNet.Models;
using LNUnit.Setup;
using NBitcoin.RPC;

namespace NLightning.Integration.Tests.Fixtures;

using Docker.Utils;

/// <summary>
/// A Core Lightning (CLN) regtest node for the interop tests, on its own bitcoind in its own Docker network, so it
/// never shares containers, names or chain state with the LND <c>regtest</c> collection and can run while another
/// test process owns miner/alice/bob/carol/david.
/// </summary>
/// <remarks>
/// bitcoind (<see cref="BitcoinContainerName"/>) publishes RPC and the ZMQ raw block/tx feeds on <c>127.0.0.1</c>
/// for the in-process NLightning nodes; CLN (<see cref="ClnContainerName"/>, the official
/// <c>elementsproject/lightningd</c> image) reaches bitcoind by name on <see cref="NetworkName"/> and publishes its
/// p2p port on <c>127.0.0.1</c>. CLN runs with <c>--developer --dev-bitcoind-poll=1</c> so it sees a new block within
/// a second (the default poll is 30 s), and with <c>--ignore-fee-limits=false</c>: on testnet/regtest CLN ignores its
/// feerate limits by default (only its mainnet config checks them), which would make any feerate we send look fine.
/// The containers and the network are force-removed before start and on dispose.
/// </remarks>
// ReSharper disable once ClassNeverInstantiated.Global
public sealed class ClnFixture : IAsyncLifetime
{
    public const string NetworkName = "nltg-cln-net";
    public const string BitcoinContainerName = "nltg-cln-bitcoind";
    public const string ClnContainerName = "nltg-cln";

    /// <summary>
    /// The CLN release the interop tests were written against (pinned so a new release is a deliberate change).
    /// </summary>
    public const string ClnImage = "elementsproject/lightningd";

    public const string ClnTag = "v26.06.8";

    /// <summary>
    /// How a container reaches a port the test process listens on (OrbStack and Docker Desktop resolve it to the host,
    /// on Linux the containers get a <c>host-gateway</c> alias; the listener must bind a non-loopback address).
    /// </summary>
    public const string HostAddressFromContainers = "host.docker.internal";

    private const string BitcoinImage = "polarlightning/bitcoind";
    private const string BitcoinTag = "29.0";
    private const string RpcUser = "nltg";
    private const string RpcPassword = "nltg";
    private const int RpcPort = 18443;
    private const int ZmqBlockPort = 28334;
    private const int ZmqTxPort = 28335;
    private const int P2PPort = 9735;

    private static readonly TimeSpan s_readyTimeout = TimeSpan.FromMinutes(2);

    private readonly DockerClient _client = new DockerClientConfiguration().CreateClient();
    private readonly SharedObjectCache _shared = new();

    private RegtestBitcoinEndpoint? _bitcoin;
    private ClnClient? _cln;

    /// <summary>
    /// The fixture's bitcoind, for <see cref="NLightningTestNode.CreateAsync(RegtestBitcoinEndpoint, string, TestNodeDatabase?, Action{Domain.Node.Options.NodeOptions}?)"/>.
    /// </summary>
    public RegtestBitcoinEndpoint Bitcoin =>
        _bitcoin ?? throw new InvalidOperationException("The CLN fixture is not running");

    public ClnClient Cln => _cln ?? throw new InvalidOperationException("The CLN fixture is not running");

    /// <summary>
    /// CLN's p2p port on <c>127.0.0.1</c>.
    /// </summary>
    public int ClnHostPort { get; private set; }

    /// <summary>
    /// CLN's node id (hex, lower case).
    /// </summary>
    public string ClnNodeId { get; private set; } = string.Empty;

    /// <summary>
    /// The <c>pubkey@127.0.0.1:port</c> an in-process node connects to.
    /// </summary>
    public string ClnAddress => $"{ClnNodeId}@127.0.0.1:{ClnHostPort}";

    /// <summary>
    /// Returns the object stored under <paramref name="key"/>, creating it once with <paramref name="factory"/> (see
    /// <see cref="LightningRegtestNetworkFixture.GetOrCreateAsync{T}"/>). Disposed with the fixture.
    /// </summary>
    public Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory) where T : class =>
        _shared.GetOrCreateAsync(key, factory);

    public async ValueTask InitializeAsync()
    {
        try
        {
            await StartAsync();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shared.DisposeAll();
        await DockerContainerUtils.RemoveContainerAsync(_client, ClnContainerName);
        await DockerContainerUtils.RemoveContainerAsync(_client, BitcoinContainerName);
        await RemoveNetworkAsync();
        _client.Dispose();
    }

    /// <summary>
    /// Mines <paramref name="blocks"/> blocks to the bitcoind wallet.
    /// </summary>
    public async Task MineAsync(int blocks, CancellationToken cancellationToken)
    {
        var rpc = Bitcoin.Rpc;
        await rpc.GenerateToAddressAsync(blocks, await rpc.GetNewAddressAsync(cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Waits until CLN and every node in <paramref name="nodes"/> have processed bitcoind's tip.
    /// </summary>
    /// <returns>The tip.</returns>
    public async Task<uint> WaitAllAtTipAsync(IEnumerable<NLightningTestNode> nodes,
                                              CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var nodeList = nodes.ToList();
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        while (true)
        {
            var tip = (uint)await Bitcoin.Rpc.GetBlockCountAsync(cancellationToken);
            var clnHeight = (uint)(await Cln.GetInfoAsync(cancellationToken))["blockheight"]!.GetValue<long>();
            var ours = nodeList.Select(n => (n.Name,
                                             Height: n.IsRunning ? n.BlockchainMonitor.LastProcessedBlockHeight : 0))
                               .ToList();
            if (clnHeight == tip && ours.All(o => o.Height == tip))
                return tip;

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(
                    $"Not at the tip {tip} in time: cln {clnHeight}, "
                  + string.Join(", ", ours.Select(o => $"{o.Name} {o.Height}")));

            await Task.Delay(200, cancellationToken);
        }
    }

    /// <summary>
    /// Sends <paramref name="amount"/> from bitcoind to a new CLN wallet address, mines 6 blocks and waits until CLN
    /// lists the output as confirmed (so a following <c>fundchannel</c> can spend it).
    /// </summary>
    public async Task FundClnWalletAsync(Domain.Money.LightningMoney amount, IEnumerable<NLightningTestNode> nodes,
                                         CancellationToken cancellationToken)
    {
        var address = (await Cln.CallAsync("newaddr", cancellationToken, ("addresstype", "bech32")))["bech32"]!
           .GetValue<string>();
        var txId = await Bitcoin.Rpc.SendToAddressAsync(
                       NBitcoin.BitcoinAddress.Create(address, NBitcoin.Network.RegTest),
                       NBitcoin.Money.Satoshis((long)amount.Satoshi), cancellationToken: cancellationToken);
        await MineAndWaitAsync(6, nodes, cancellationToken);
        await Poll.UntilAsync(async () =>
        {
            var outputs = (await Cln.CallAsync("listfunds", cancellationToken))["outputs"]!.AsArray();
            return outputs.Any(o => o?["txid"]?.GetValue<string>() == txId.ToString()
                                 && o["status"]?.GetValue<string>() == "confirmed");
        }, TimeSpan.FromSeconds(60), "CLN sees its deposit confirmed", cancellationToken);
    }

    /// <summary>
    /// Mines <paramref name="blocks"/> blocks, then waits until CLN and <paramref name="nodes"/> are at the tip.
    /// </summary>
    public async Task<uint> MineAndWaitAsync(int blocks, IEnumerable<NLightningTestNode> nodes,
                                             CancellationToken cancellationToken)
    {
        await MineAsync(blocks, cancellationToken);
        return await WaitAllAtTipAsync(nodes, cancellationToken);
    }

    private async Task StartAsync()
    {
        await _client.PullImageAndWaitForCompleted(BitcoinImage, BitcoinTag);
        await _client.PullImageAndWaitForCompleted(ClnImage, ClnTag);

        await DockerContainerUtils.RemoveContainerAsync(_client, ClnContainerName);
        await DockerContainerUtils.RemoveContainerAsync(_client, BitcoinContainerName);
        await RemoveNetworkAsync();
        await _client.Networks.CreateNetworkAsync(new NetworksCreateParameters
        {
            Name = NetworkName,
            Driver = "bridge"
        });

        // bitcoind
        var bitcoinPorts = await StartContainerAsync($"{BitcoinImage}:{BitcoinTag}", BitcoinContainerName, [],
                                                     [
                                                         "bitcoind", "-regtest", "-server=1",
                                                         $"-rpcuser={RpcUser}", $"-rpcpassword={RpcPassword}",
                                                         "-rpcbind=0.0.0.0", "-rpcallowip=0.0.0.0/0",
                                                         $"-rpcport={RpcPort}", "-rpcworkqueue=1024",
                                                         $"-zmqpubrawblock=tcp://0.0.0.0:{ZmqBlockPort}",
                                                         $"-zmqpubrawtx=tcp://0.0.0.0:{ZmqTxPort}",
                                                         "-txindex=1", "-fallbackfee=0.0002", "-dnsseed=0",
                                                         "-listen=0", "-printtoconsole"
                                                     ], [RpcPort, ZmqBlockPort, ZmqTxPort]);
        var rpc = new RPCClient($"{RpcUser}:{RpcPassword}", $"http://127.0.0.1:{bitcoinPorts[RpcPort]}",
                                NBitcoin.Network.RegTest);
        await DockerContainerUtils.WaitUntilReadyAsync(BitcoinContainerName,
                                                       async ct => await rpc.GetBlockCountAsync(ct), s_readyTimeout);
        await rpc.SendCommandAsync("createwallet", "miner");
        _bitcoin = new RegtestBitcoinEndpoint(rpc, "127.0.0.1", bitcoinPorts[ZmqBlockPort], bitcoinPorts[ZmqTxPort]);
        await MineAsync(101, CancellationToken.None);

        // CLN
        var clnPorts = await StartContainerAsync($"{ClnImage}:{ClnTag}", ClnContainerName,
                                                 ["LIGHTNINGD_NETWORK=regtest"],
                                                 [
                                                     $"--bitcoin-rpcconnect={BitcoinContainerName}",
                                                     $"--bitcoin-rpcport={RpcPort}",
                                                     $"--bitcoin-rpcuser={RpcUser}",
                                                     $"--bitcoin-rpcpassword={RpcPassword}",
                                                     $"--bind-addr=0.0.0.0:{P2PPort}",
                                                     "--alias=nltg-cln",
                                                     "--log-level=debug",
                                                     "--developer",
                                                     "--dev-bitcoind-poll=1",
                                                     // CLN's testnet/regtest default is ignore-fee-limits=true (only
                                                     // mainnet checks them): turn the checks on, so open_channel and
                                                     // update_fee meet CLN's real feerate range as on mainnet
                                                     "--ignore-fee-limits=false"
                                                 ], [P2PPort]);
        ClnHostPort = clnPorts[P2PPort];
        var cln = new ClnClient(_client, ClnContainerName);
        await DockerContainerUtils.WaitUntilReadyAsync(ClnContainerName, async ct => await cln.GetInfoAsync(ct),
                                                       s_readyTimeout);
        _cln = cln;
        ClnNodeId = (await cln.GetInfoAsync(CancellationToken.None))["id"]!.GetValue<string>();
        await WaitAllAtTipAsync([], CancellationToken.None);
    }

    /// <summary>
    /// Creates and starts a container on <see cref="NetworkName"/> with <paramref name="containerPorts"/> published on
    /// free <c>127.0.0.1</c> ports.
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
                NetworkMode = NetworkName,
                PortBindings = containerPorts.ToDictionary(
                    p => $"{p}/tcp",
                    IList<PortBinding> (_) => [new PortBinding { HostIP = "127.0.0.1", HostPort = string.Empty }]),
                // OrbStack and Docker Desktop resolve host.docker.internal themselves; plain Linux Docker needs the alias
                ExtraHosts = OperatingSystem.IsLinux() ? [$"{HostAddressFromContainers}:host-gateway"] : null
            }
        };

        var container = await _client.Containers.CreateContainerAsync(parameters)
                     ?? throw new InvalidOperationException($"Failed to create the {name} container");
        await _client.Containers.StartContainerAsync(container.ID, new ContainerStartParameters());

        var deadline = DateTime.UtcNow.AddMinutes(1);
        while (true)
        {
            var inspect = await _client.Containers.InspectContainerAsync(container.ID);
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
            await _client.Networks.DeleteNetworkAsync(NetworkName);
        }
        catch
        {
            // ignored: not there
        }
    }
}