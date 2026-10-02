namespace NLightning.Testing.Cluster.Nodes.BitcoinCore;

using Images;
using Kube;

/// <summary>
/// A regtest bitcoind of a run (plan R7 <c>BitcoinCore</c>). The defaults match the Docker fixtures
/// (<c>InteropChainHost</c>, <c>ClnFixture</c>): user/password <c>nltg</c>, <c>-txindex</c>, <c>-fallbackfee=0.0002</c>,
/// ZMQ raw block, raw tx and hash block feeds, and a <c>miner</c> wallet.
/// </summary>
public sealed record BitcoinCoreOptions
{
    /// <summary>The node's alias (StatefulSet, Service and container name).</summary>
    public string Name { get; init; } = "miner";

    /// <summary>The image: <see cref="ImageVersions.BitcoinCore"/> (29.0) or <see cref="ImageVersions.BitcoinCore31"/>.</summary>
    public ImageRef Image { get; init; } = ImageVersions.BitcoinCore;

    public string RpcUser { get; init; } = "nltg";

    public string RpcPassword { get; init; } = "nltg";

    /// <summary>
    /// The wallet created at deploy (loaded at every start, so it survives restarts on the PVC) and used by the RPC
    /// clients, or null for a node without a wallet (a follower).
    /// </summary>
    public string? Wallet { get; init; } = "miner";

    /// <summary><c>-txindex</c>: lets <c>getrawtransaction</c> find any confirmed transaction (the tx waits need it).</summary>
    public bool TxIndex { get; init; } = true;

    /// <summary><c>-fallbackfee</c> in BTC/kvB (the wallet's fee rate while there is no estimate), or null for none.</summary>
    public decimal? FallbackFeeBtcPerKvB { get; init; } = 0.0002m;

    /// <summary>
    /// Other bitcoind nodes of the run to peer with (<c>-addnode</c>, alias or <c>alias:port</c>); followers of a
    /// miner list it here.
    /// </summary>
    public IReadOnlyList<string> AddNodes { get; init; } = [];

    /// <summary>More bitcoind arguments (e.g. <c>-minrelaytxfee=...</c>), appended last.</summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];

    public WorkloadResources Resources { get; init; } = WorkloadResources.Default;

    /// <summary>The data PVC's size.</summary>
    public string DataSize { get; init; } = "2Gi";

    /// <summary>The data PVC's storage class, or null for the cluster default.</summary>
    public string? StorageClassName { get; init; }

    /// <summary>The data directory (both images' <c>/home/bitcoin/.bitcoin</c>), the PVC's mount path.</summary>
    public string DataPath { get; init; } = "/home/bitcoin/.bitcoin";

    /// <summary>The readiness probe's period: the node is ready when <c>getblockchaininfo</c> answers.</summary>
    public int ReadinessPeriodSeconds { get; init; } = 1;
}

/// <summary>
/// bitcoind's ports inside its pod. Every pod has its own IP, so they are fixed (no port pool, plan R5).
/// </summary>
public static class BitcoinCorePorts
{
    public const int Rpc = 18443;
    public const int P2p = 18444;
    public const int ZmqRawBlock = 28332;
    public const int ZmqRawTx = 28333;

    /// <summary>The hash block feed (Eclair's <c>zmqblock</c>).</summary>
    public const int ZmqHashBlock = 28334;
}