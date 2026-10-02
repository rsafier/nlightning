using System.Globalization;
using System.Text.Json.Nodes;

namespace NLightning.Testing.Cluster.Topology;

using Images;
using Kube;
using Nodes;
using Run;

/// <summary>
/// The spike's regtest bitcoind for a topology (the flags of <c>ClnFixture</c>: RPC on 18443, ZMQ raw block/tx,
/// txindex, fallback fee), its data on a PVC, ready when its RPC answers, driven by <c>bitcoin-cli</c> in the pod. A
/// stopgap the chain lane's Bitcoin Core node replaces through <see cref="TopologyBuilder.UseChain"/>.
/// </summary>
public sealed class TopologyBitcoind : ITopologyChain
{
    public const string DataPath = "/home/bitcoin/.bitcoin";
    public const string WalletName = "miner";
    public const string DefaultRpcUser = "nltg";
    public const string DefaultRpcPassword = "nltg";
    public const int DefaultRpcPort = 18443;
    public const int ZmqBlockPort = 28334;
    public const int ZmqTxPort = 28335;

    /// <summary>The blocks mined at start so the wallet's first coinbase is spendable.</summary>
    public const int MaturityBlocks = 101;

    public TopologyBitcoind(INodeHandle node)
    {
        Node = node ?? throw new ArgumentNullException(nameof(node));
    }

    public INodeHandle Node { get; }

    public string RpcHost => Node.Name;

    public int RpcPort => DefaultRpcPort;

    public string RpcUser => DefaultRpcUser;

    public string RpcPassword => DefaultRpcPassword;

    /// <summary>The workload of the chain node <paramref name="name"/>.</summary>
    public static NodeWorkload Workload(string name, ImageRef? image = null, IReadOnlyList<string>? extraArgs = null)
    {
        var workload = new NodeWorkload(name, NodeKind.BitcoinCore, image ?? ImageVersions.BitcoinCore)
        {
            Data = new DataVolume(DataPath),
            ReadinessProbe = Probes.Exec(CliCommand(null, ["getblockchaininfo"]), periodSeconds: 1),
            TerminationGracePeriodSeconds = 15
        };
        workload.Ports.Add(new WorkloadPort("rpc", DefaultRpcPort));
        workload.Ports.Add(new WorkloadPort("p2p", 18444));
        workload.Ports.Add(new WorkloadPort("zmq-block", ZmqBlockPort));
        workload.Ports.Add(new WorkloadPort("zmq-tx", ZmqTxPort));
        foreach (var arg in BuildArgs(extraArgs ?? []))
            workload.Args.Add(arg);

        return workload;
    }

    /// <summary>The image's command: <c>bitcoind</c> and its flags (the entrypoint runs it as the bitcoin user).</summary>
    public static IReadOnlyList<string> BuildArgs(IReadOnlyList<string> extraArgs) =>
    [
        "bitcoind", "-regtest", "-server=1", $"-rpcuser={DefaultRpcUser}", $"-rpcpassword={DefaultRpcPassword}",
        "-rpcbind=0.0.0.0", "-rpcallowip=0.0.0.0/0", $"-rpcport={DefaultRpcPort}", "-rpcworkqueue=1024",
        $"-zmqpubrawblock=tcp://0.0.0.0:{ZmqBlockPort}", $"-zmqpubrawtx=tcp://0.0.0.0:{ZmqTxPort}", "-txindex=1",
        "-fallbackfee=0.0002", "-dnsseed=0", "-listen=0", "-printtoconsole", .. extraArgs
    ];

    /// <summary>A <c>bitcoin-cli</c> command line (against the wallet <paramref name="wallet"/> when given).</summary>
    public static IReadOnlyList<string> CliCommand(string? wallet, IEnumerable<string> args)
    {
        List<string> command =
        [
            "bitcoin-cli", "-regtest", $"-rpcuser={DefaultRpcUser}", $"-rpcpassword={DefaultRpcPassword}",
            $"-rpcport={DefaultRpcPort}"
        ];
        if (wallet is not null)
            command.Add($"-rpcwallet={wallet}");
        command.AddRange(args);
        return command;
    }

    /// <summary>A satoshi amount as the BTC decimal RPC takes (<c>0.00100000</c>).</summary>
    public static string FormatBtc(long amountSat)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amountSat);
        return (amountSat / 100_000_000m).ToString("0.00000000", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// <see cref="TopologyBuilder.ChainFactory"/>: deploys the node, creates (or loads) the miner wallet, which then
    /// loads at every start, and mines <see cref="MaturityBlocks"/> blocks on a fresh chain.
    /// </summary>
    public static async Task<ITopologyChain> DeployAsync(TestRun run, TopologyNodeSpec chainNode,
                                                         TimeSpan readyTimeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(chainNode);

        var node = await run.DeployAsync(Workload(chainNode.Name, chainNode.Image, chainNode.Args), readyTimeout,
                                         cancellationToken)
                            .ConfigureAwait(false);
        var chain = new TopologyBitcoind(node);
        await chain.EnsureWalletAsync(cancellationToken).ConfigureAwait(false);
        var height = await chain.GetBlockCountAsync(cancellationToken).ConfigureAwait(false);
        if (height < MaturityBlocks)
            await chain.MineAsync(MaturityBlocks - (int)height, cancellationToken).ConfigureAwait(false);

        return chain;
    }

    /// <summary>Runs <c>bitcoin-cli</c> and returns its trimmed output.</summary>
    /// <exception cref="KubeExecException">bitcoin-cli failed (its stderr carries the RPC error).</exception>
    public async Task<string> CliAsync(string? wallet, CancellationToken cancellationToken, params string[] args)
    {
        var result = await Node.ExecAsync(CliCommand(wallet, args), cancellationToken).ConfigureAwait(false);
        return result.EnsureSuccess($"bitcoin-cli {string.Join(' ', args.Take(1))}").StdOutText.Trim();
    }

    public async Task<long> GetBlockCountAsync(CancellationToken cancellationToken) =>
        long.Parse(await CliAsync(null, cancellationToken, "getblockcount").ConfigureAwait(false),
                   CultureInfo.InvariantCulture);

    public async Task<IReadOnlyList<string>> MineAsync(int blocks, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blocks);
        var address = await CliAsync(WalletName, cancellationToken, "getnewaddress").ConfigureAwait(false);
        var hashes = JsonNode.Parse(await CliAsync(WalletName, cancellationToken, "generatetoaddress",
                                                   blocks.ToString(CultureInfo.InvariantCulture), address)
                                       .ConfigureAwait(false))!.AsArray();
        return hashes.Select(h => h!.GetValue<string>()).ToList();
    }

    public Task<string> SendToAddressAsync(string address, long amountSat, CancellationToken cancellationToken) =>
        CliAsync(WalletName, cancellationToken, "sendtoaddress", address, FormatBtc(amountSat));

    private async Task EnsureWalletAsync(CancellationToken cancellationToken)
    {
        var loaded = JsonNode.Parse(await CliAsync(null, cancellationToken, "listwallets").ConfigureAwait(false))!
                             .AsArray();
        if (loaded.Any(w => w?.GetValue<string>() == WalletName))
            return;

        var existing = JsonNode.Parse(await CliAsync(null, cancellationToken, "listwalletdir").ConfigureAwait(false))!
                               ["wallets"]!.AsArray();
        if (existing.Any(w => w?["name"]?.GetValue<string>() == WalletName))
            await CliAsync(null, cancellationToken, "-named", "loadwallet", $"filename={WalletName}",
                           "load_on_startup=true")
               .ConfigureAwait(false);
        else
            await CliAsync(null, cancellationToken, "-named", "createwallet", $"wallet_name={WalletName}",
                           "load_on_startup=true")
               .ConfigureAwait(false);
    }
}