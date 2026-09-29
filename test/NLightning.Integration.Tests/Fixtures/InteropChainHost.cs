using System.Formats.Tar;
using Docker.DotNet;
using Docker.DotNet.Models;
using NBitcoin.RPC;

namespace NLightning.Integration.Tests.Fixtures;

using Docker.Utils;

/// <summary>
/// The chain side of an interop fixture (Eclair, LDK; NL-180): its own Docker network and a bitcoind 31.1 on it,
/// never shared with the LND <c>regtest</c> collection or the CLN fixture. Copied from <see cref="ClnFixture"/> (which
/// stays as it is), plus the <c>hashblock</c> ZMQ feed Eclair needs and fixed host ports for a peer whose address must
/// survive a container restart.
/// </summary>
/// <remarks>
/// bitcoind publishes RPC and the ZMQ raw block/tx feeds on <c>127.0.0.1</c> for the in-process NLightning nodes; the
/// peer container reaches it by name on the network. The image is pinned by digest (Eclair 0.14.3 refuses Core older
/// than 31).
/// </remarks>
public sealed class InteropChainHost(DockerClient client, string networkName, string bitcoindName)
{
    public const string BitcoinImage = "bitcoin/bitcoin";

    /// <summary>Bitcoin Core 31.1, multi-arch index digest.</summary>
    public const string BitcoinDigest = "sha256:da25cedc66b1daefff9f412ee196c901a899c3fa68a33b20849c3e08b5c40d63";

    public const string RpcUser = "nltg";
    public const string RpcPassword = "nltg";
    public const int RpcPort = 18443;
    public const int ZmqBlockPort = 28334;
    public const int ZmqTxPort = 28335;

    /// <summary>The <c>hashblock</c> feed (Eclair's <c>zmqblock</c>), reachable only inside the network.</summary>
    public const int ZmqHashBlockPort = 28336;

    private static readonly TimeSpan s_readyTimeout = TimeSpan.FromMinutes(2);

    private RegtestBitcoinEndpoint? _bitcoin;

    public string NetworkName { get; } = networkName;

    public string BitcoindName { get; } = bitcoindName;

    public RegtestBitcoinEndpoint Bitcoin =>
        _bitcoin ?? throw new InvalidOperationException($"The bitcoind {BitcoindName} is not running");

    /// <summary>
    /// Removes what an earlier run left, creates the network, starts bitcoind, creates the <c>miner</c> wallet and
    /// mines 101 blocks.
    /// </summary>
    public async Task StartAsync()
    {
        const string image = $"{BitcoinImage}@{BitcoinDigest}";
        await EnsureImageAsync(client, BitcoinImage, BitcoinDigest);

        await DockerContainerUtils.RemoveContainerAsync(client, BitcoindName);
        await RemoveNetworkAsync();
        await client.Networks.CreateNetworkAsync(new NetworksCreateParameters { Name = NetworkName, Driver = "bridge" });

        // The image's entrypoint runs bitcoind when the first argument is an option
        var ports = await StartContainerAsync(image, BitcoindName, [],
                                              [
                                                  "-regtest", "-server=1",
                                                  $"-rpcuser={RpcUser}", $"-rpcpassword={RpcPassword}",
                                                  "-rpcbind=0.0.0.0", "-rpcallowip=0.0.0.0/0",
                                                  $"-rpcport={RpcPort}", "-rpcworkqueue=1024",
                                                  $"-zmqpubrawblock=tcp://0.0.0.0:{ZmqBlockPort}",
                                                  $"-zmqpubrawtx=tcp://0.0.0.0:{ZmqTxPort}",
                                                  $"-zmqpubhashblock=tcp://0.0.0.0:{ZmqHashBlockPort}",
                                                  "-txindex=1", "-fallbackfee=0.0002", "-dnsseed=0",
                                                  "-listen=0", "-printtoconsole"
                                              ], [RpcPort, ZmqBlockPort, ZmqTxPort]);
        var rpc = new RPCClient($"{RpcUser}:{RpcPassword}", $"http://127.0.0.1:{ports[RpcPort]}",
                                NBitcoin.Network.RegTest);
        await DockerContainerUtils.WaitUntilReadyAsync(BitcoindName, async ct => await rpc.GetBlockCountAsync(ct),
                                                       s_readyTimeout);
        await rpc.SendCommandAsync("createwallet", "miner");
        // Scoped to the miner wallet: Eclair's wallet is loaded next to it, and wallet calls without a wallet path
        // fail once several are loaded (node calls work on a wallet path too)
        _bitcoin = new RegtestBitcoinEndpoint(rpc.SetWalletContext("miner"), "127.0.0.1", ports[ZmqBlockPort],
                                              ports[ZmqTxPort]);
        await MineAsync(101, CancellationToken.None);
    }

    /// <summary>
    /// Creates a bitcoind wallet for a peer that funds from bitcoind (Eclair).
    /// </summary>
    public async Task CreateWalletAsync(string name) => await Bitcoin.Rpc.SendCommandAsync("createwallet", name);

    /// <summary>
    /// Mines <paramref name="blocks"/> blocks to the miner wallet.
    /// </summary>
    public async Task MineAsync(int blocks, CancellationToken cancellationToken)
    {
        var rpc = Bitcoin.Rpc;
        await rpc.GenerateToAddressAsync(blocks, await rpc.GetNewAddressAsync(cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Sends <paramref name="satoshis"/> from the miner wallet to <paramref name="address"/>.
    /// </summary>
    public async Task<NBitcoin.uint256> SendToAddressAsync(string address, long satoshis,
                                                            CancellationToken cancellationToken) =>
        await Bitcoin.Rpc.SendToAddressAsync(NBitcoin.BitcoinAddress.Create(address, NBitcoin.Network.RegTest),
                                             NBitcoin.Money.Satoshis(satoshis), cancellationToken: cancellationToken);

    public async Task<uint> GetTipAsync(CancellationToken cancellationToken) =>
        (uint)await Bitcoin.Rpc.GetBlockCountAsync(cancellationToken);

    /// <summary>
    /// Removes bitcoind and the network (the caller removes its own peer containers first).
    /// </summary>
    public async Task RemoveAsync()
    {
        await DockerContainerUtils.RemoveContainerAsync(client, BitcoindName);
        await RemoveNetworkAsync();
    }

    /// <summary>
    /// Pulls <paramref name="repository"/> at <paramref name="tagOrDigest"/> unless it is already present.
    /// </summary>
    public static async Task EnsureImageAsync(DockerClient docker, string repository, string tagOrDigest)
    {
        var reference = tagOrDigest.StartsWith("sha256:", StringComparison.Ordinal)
                            ? $"{repository}@{tagOrDigest}"
                            : $"{repository}:{tagOrDigest}";
        if (await ImageExistsAsync(docker, reference))
            return;

        await docker.Images.CreateImageAsync(new ImagesCreateParameters { FromImage = repository, Tag = tagOrDigest },
                                             null, new Progress<JSONMessage>());
        if (!await ImageExistsAsync(docker, reference))
            throw new InvalidOperationException($"Could not pull {reference}");
    }

    public static async Task<bool> ImageExistsAsync(DockerClient docker, string reference)
    {
        try
        {
            await docker.Images.InspectImageAsync(reference);
            return true;
        }
        catch (DockerImageNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates and starts a container on <see cref="NetworkName"/>. <paramref name="containerPorts"/> are published on
    /// <c>127.0.0.1</c>, each on its <paramref name="fixedHostPorts"/> entry when given (so it survives a restart),
    /// otherwise on a free port. <paramref name="files"/> (path in the container, content) are copied in before start.
    /// </summary>
    /// <returns>The host port of each container port.</returns>
    public async Task<Dictionary<int, int>> StartContainerAsync(string image, string name, IList<string> env,
                                                                IList<string> cmd, IReadOnlyList<int> containerPorts,
                                                                IReadOnlyDictionary<int, int>? fixedHostPorts = null,
                                                                IReadOnlyDictionary<string, string>? files = null)
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
                    IList<PortBinding> (p) =>
                    [
                        new PortBinding
                        {
                            HostIP = "127.0.0.1",
                            HostPort = fixedHostPorts is not null && fixedHostPorts.TryGetValue(p, out var hostPort)
                                           ? hostPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                           : string.Empty
                        }
                    ]),
                // OrbStack and Docker Desktop resolve host.docker.internal themselves; plain Linux Docker needs the alias
                ExtraHosts = OperatingSystem.IsLinux() ? [$"{ClnFixture.HostAddressFromContainers}:host-gateway"] : null
            }
        };

        var container = await client.Containers.CreateContainerAsync(parameters)
                     ?? throw new InvalidOperationException($"Failed to create the {name} container");
        if (files is not null)
        {
            foreach (var (path, content) in files)
                await CopyFileAsync(container.ID, path, content);
        }

        await client.Containers.StartContainerAsync(container.ID, new ContainerStartParameters());
        return await WaitForPortsAsync(container.ID, name, containerPorts);
    }

    /// <summary>
    /// Restarts a container (its file system, and so the peer's state, is kept).
    /// </summary>
    public async Task RestartContainerAsync(string name) =>
        await client.Containers.RestartContainerAsync(name, new ContainerRestartParameters { WaitBeforeKillSeconds = 10 });

    private async Task CopyFileAsync(string containerId, string path, string content)
    {
        using var tar = new MemoryStream();
        await using (var writer = new TarWriter(tar, TarEntryFormat.Pax, leaveOpen: true))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, Path.GetFileName(path))
            {
                DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)),
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead
            };
            await writer.WriteEntryAsync(entry);
        }

        tar.Position = 0;
        await client.Containers.ExtractArchiveToContainerAsync(
            containerId, new ContainerPathStatParameters { Path = Path.GetDirectoryName(path)! }, tar);
    }

    private async Task<Dictionary<int, int>> WaitForPortsAsync(string containerId, string name,
                                                               IReadOnlyList<int> containerPorts)
    {
        var deadline = DateTime.UtcNow.AddMinutes(1);
        while (true)
        {
            var inspect = await client.Containers.InspectContainerAsync(containerId);
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
            await client.Networks.DeleteNetworkAsync(NetworkName);
        }
        catch
        {
            // ignored: not there
        }
    }
}