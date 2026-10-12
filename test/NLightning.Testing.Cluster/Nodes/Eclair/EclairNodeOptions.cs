using k8s.Models;

namespace NLightning.Testing.Cluster.Nodes.Eclair;

using BitcoinCore;
using Images;
using Kube;

/// <summary>
/// How an Eclair node of a topology runs: its image (the locally built <c>nltg-eclair:0.14.3</c>, never pulled), the
/// bitcoind it funds from and follows, and the settings <c>EclairFixture</c> uses on Docker (API password, a bitcoind
/// wallet of its own, ZMQ <c>hashblock</c> and <c>rawtx</c>, 6 confirmations).
/// </summary>
public sealed record EclairNodeOptions
{
    /// <summary>The image; the version table's Eclair 0.14.3 by default.</summary>
    public ImageRef Image { get; init; } = ImageVersions.Eclair;

    /// <summary>The bitcoind Service Eclair calls and subscribes to (the chain node's alias).</summary>
    public required string BitcoindHost { get; init; }

    public int BitcoindRpcPort { get; init; } = BitcoinCorePorts.Rpc;

    public required string BitcoindRpcUser { get; init; }

    public required string BitcoindRpcPassword { get; init; }

    /// <summary>bitcoind's <c>zmqpubhashblock</c> port (Eclair's <c>zmqblock</c>).</summary>
    public int ZmqHashBlockPort { get; init; } = BitcoinCorePorts.ZmqHashBlock;

    /// <summary>bitcoind's <c>zmqpubrawtx</c> port (Eclair's <c>zmqtx</c>).</summary>
    public int ZmqRawTxPort { get; init; } = BitcoinCorePorts.ZmqRawTx;

    /// <summary>
    /// The bitcoind wallet Eclair funds channels from (<c>eclair.bitcoind.wallet</c>). The node's init container
    /// creates it (or loads it) before Eclair starts (<see cref="EclairNode.WalletInitScript"/>).
    /// </summary>
    public string Wallet { get; init; } = "eclair";

    /// <summary>The node's alias in gossip; the node name when null.</summary>
    public string? Alias { get; init; }

    /// <summary>The API password (<c>eclair.api.password</c>; HTTP basic auth with an empty user).</summary>
    public string ApiPassword { get; init; } = "nltg";

    /// <summary><c>eclair.channel.min-depth-blocks</c> (the Docker fixture's 6).</summary>
    public int MinDepthBlocks { get; init; } = 6;

    /// <summary>More <c>eclair.conf</c> lines (HOCON), appended after the defaults.</summary>
    public IReadOnlyList<string> ExtraConfig { get; init; } = [];

    /// <summary>The JVM options (<c>JAVA_OPTS</c>), as the Docker fixture sets them.</summary>
    public string JavaOptions { get; init; } = "-Xmx512m -Declair.printToConsole=true";

    /// <summary>The size of the data PVC (<see cref="EclairNode.DataPath"/>).</summary>
    public string DataSize { get; init; } = "1Gi";

    /// <summary>
    /// The data directory on a PVC (default: a restart keeps the node id and its channels) or in an <c>emptyDir</c>
    /// (<see cref="NodeStorage.Ephemeral"/>).
    /// </summary>
    public NodeStorage Storage { get; init; } = NodeStorage.Persistent;

    /// <summary>
    /// An init container that holds Eclair until bitcoind answers
    /// (<see cref="Topology.ITopologyChainEndpoint.CreateStartupWait"/>), or null to start at once (the wallet's init
    /// container waits too).
    /// </summary>
    public V1Container? StartupWait { get; init; }

    /// <summary>
    /// The JVM's resources: requests within the spike's 1 CPU / 1 GiB per pod, limits above the default so the JVM
    /// starts in seconds and its 512 MiB heap plus metaspace fit.
    /// </summary>
    public WorkloadResources Resources { get; init; } = new("250m", "512Mi", "2", "1536Mi");
}