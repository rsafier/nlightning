using System.Globalization;

namespace NLightning.Testing.Cluster.Topology.Lnd;

using Images;
using Kube;
using Nodes;
using Run;

/// <summary>
/// The regtest bitcoind of the LND topologies (LNUnit's <c>AddBitcoinCoreNode</c>: Polar's Bitcoin Core 29.0 named
/// <c>miner</c>, RPC 18443, ZMQ raw blocks/txs on 28334/28335) and the few chain calls they need, run with
/// <c>bitcoin-cli</c> in the pod. A stand-in for the lane that builds the shared <c>BitcoinCore</c> node and the
/// <c>Chain/</c> helpers (plan R10); the topology switches to those when they land.
/// </summary>
public sealed class LndTopologyChain
{
    public const string DefaultName = "miner";
    public const string RpcUser = "bitcoin";
    public const string RpcPassword = "bitcoin";
    public const int RpcPort = 18443;
    public const int P2pPort = 18444;
    public const int ZmqRawBlockPort = 28334;
    public const int ZmqRawTxPort = 28335;
    public const string DataDir = "/home/bitcoin/.bitcoin";
    public const string WalletName = "miner";

    private string? _miningAddress;

    public LndTopologyChain(INodeHandle node)
    {
        Node = node ?? throw new ArgumentNullException(nameof(node));
    }

    public INodeHandle Node { get; }

    /// <summary>The <c>bitcoin-cli</c> prefix (RPC over the pod's loopback; the probe and exec run as root).</summary>
    public static IReadOnlyList<string> CliPrefix { get; } =
    [
        "bitcoin-cli", "-regtest", $"-rpcuser={RpcUser}", $"-rpcpassword={RpcPassword}",
        $"-rpcport={RpcPort.ToString(CultureInfo.InvariantCulture)}"
    ];

    /// <summary>bitcoind's workload: data on the PVC, ready when its RPC answers.</summary>
    public static NodeWorkload BuildWorkload(string name = DefaultName, ImageRef? image = null)
    {
        var workload = new NodeWorkload(name, NodeKind.BitcoinCore, image ?? ImageVersions.BitcoinCore)
        {
            Data = new DataVolume(DataDir, "1Gi"),
            ReadinessProbe = Probes.Exec([.. CliPrefix, "getblockchaininfo"], periodSeconds: 1, timeoutSeconds: 5),
            TerminationGracePeriodSeconds = 15
        };
        foreach (var arg in BuildArgs())
            workload.Args.Add(arg);
        workload.Ports.Add(new WorkloadPort("rpc", RpcPort));
        workload.Ports.Add(new WorkloadPort("p2p", P2pPort));
        workload.Ports.Add(new WorkloadPort("zmq-block", ZmqRawBlockPort));
        workload.Ports.Add(new WorkloadPort("zmq-tx", ZmqRawTxPort));
        return workload;
    }

    /// <summary>bitcoind's command line (LNUnit's, with a plain rpcuser/rpcpassword instead of its rpcauth hash).</summary>
    public static IReadOnlyList<string> BuildArgs() =>
    [
        "bitcoind",
        "-server=1",
        "-regtest=1",
        $"-rpcuser={RpcUser}",
        $"-rpcpassword={RpcPassword}",
        "-zmqpubrawblock=tcp://0.0.0.0:" + ZmqRawBlockPort.ToString(CultureInfo.InvariantCulture),
        "-zmqpubrawtx=tcp://0.0.0.0:" + ZmqRawTxPort.ToString(CultureInfo.InvariantCulture),
        "-txindex=1",
        "-dnsseed=0",
        "-natpmp=0",
        "-rpcbind=0.0.0.0",
        "-rpcallowip=0.0.0.0/0",
        "-rpcport=" + RpcPort.ToString(CultureInfo.InvariantCulture),
        "-rest",
        "-rpcworkqueue=1024",
        "-listen=1",
        "-listenonion=0",
        "-fallbackfee=0.0002",
        "-printtoconsole"
    ];

    /// <summary>Deploys the chain into <paramref name="run"/> and creates (or loads, after a restart) its wallet.</summary>
    public static async Task<LndTopologyChain> DeployAsync(TestRun run, TimeSpan readyTimeout,
                                                           CancellationToken cancellationToken,
                                                           string name = DefaultName)
    {
        ArgumentNullException.ThrowIfNull(run);

        var handle = await run.DeployAsync(BuildWorkload(name), readyTimeout, cancellationToken)
                              .ConfigureAwait(false);
        var chain = new LndTopologyChain(handle);
        await chain.EnsureWalletAsync(cancellationToken).ConfigureAwait(false);
        return chain;
    }

    /// <summary>Creates the miner wallet, or loads it when it exists on the PVC.</summary>
    public async Task EnsureWalletAsync(CancellationToken cancellationToken)
    {
        var created = await Node.ExecAsync([.. CliPrefix, "createwallet", WalletName], cancellationToken)
                                .ConfigureAwait(false);
        if (created.Succeeded)
            return;

        var loaded = await Node.ExecAsync([.. CliPrefix, "loadwallet", WalletName], cancellationToken)
                               .ConfigureAwait(false);
        if (!loaded.Succeeded && !loaded.StdErrText.Contains("already loaded", StringComparison.Ordinal))
            loaded.EnsureSuccess($"createwallet/loadwallet {WalletName}");
    }

    /// <summary>Runs a <c>bitcoin-cli</c> call on the miner wallet and returns its trimmed output.</summary>
    public async Task<string> CliAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var result = await Node.ExecAsync([.. CliPrefix, $"-rpcwallet={WalletName}", .. args], cancellationToken)
                               .ConfigureAwait(false);
        return result.EnsureSuccess($"bitcoin-cli {string.Join(' ', args)}").StdOutText.Trim();
    }

    /// <summary>Mines <paramref name="blocks"/> blocks to the miner wallet and returns the new height.</summary>
    public async Task<int> MineAsync(int blocks, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blocks);

        _miningAddress ??= await CliAsync(["getnewaddress", "", "bech32"], cancellationToken).ConfigureAwait(false);
        await CliAsync(["generatetoaddress", blocks.ToString(CultureInfo.InvariantCulture), _miningAddress],
                       cancellationToken).ConfigureAwait(false);
        return await GetBlockCountAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends <paramref name="amountBtc"/> to <paramref name="address"/> and returns the txid.</summary>
    public Task<string> SendToAddressAsync(string address, decimal amountBtc, CancellationToken cancellationToken) =>
        CliAsync(["sendtoaddress", address, amountBtc.ToString(CultureInfo.InvariantCulture)], cancellationToken);

    public async Task<int> GetBlockCountAsync(CancellationToken cancellationToken) =>
        int.Parse(await CliAsync(["getblockcount"], cancellationToken).ConfigureAwait(false),
                  CultureInfo.InvariantCulture);

    /// <summary>Waits until <paramref name="txId"/> is in the mempool.</summary>
    public async Task WaitForMempoolAsync(string txId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var entry = await Node.ExecAsync([.. CliPrefix, "getmempoolentry", txId], cancellationToken)
                                  .ConfigureAwait(false);
            if (entry.Succeeded)
                return;
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"{txId} not in {Node.Name}'s mempool after {timeout}");

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }
    }
}