using k8s.Models;

namespace NLightning.Testing.Cluster.Topology;

using Nodes;

/// <summary>
/// Where the Lightning nodes of a topology reach the chain: known from the topology's spec before the chain node is
/// up, so the nodes can be deployed together with it (<see cref="TopologyBuilder.DeployNodesWithChain"/>).
/// <see cref="ITopologyChain"/> is the started chain; <see cref="BitcoinCoreChainEndpoint"/> the default's endpoint.
/// </summary>
public interface ITopologyChainEndpoint
{
    /// <summary>The host the Lightning nodes call (the chain node's alias, resolved inside the run's namespace).</summary>
    string RpcHost { get; }

    int RpcPort { get; }

    string RpcUser { get; }

    string RpcPassword { get; }

    /// <summary>bitcoind's <c>zmqpubrawblock</c> port on <see cref="RpcHost"/> (LND follows the chain over ZMQ).</summary>
    int ZmqRawBlockPort { get; }

    /// <summary>bitcoind's <c>zmqpubrawtx</c> port on <see cref="RpcHost"/>.</summary>
    int ZmqRawTxPort { get; }

    /// <summary>
    /// An init container that holds a Lightning node's start until the chain answers RPC (it gives up after a while
    /// and lets the node start anyway), or null when the nodes need not wait. LND exits when bitcoind does not answer
    /// at start (the pod would go into <c>CrashLoopBackOff</c>), so the deployers add it to every node they start
    /// before or with the chain; it costs one RPC call when the chain is already up.
    /// </summary>
    V1Container? CreateStartupWait() => null;
}

/// <summary>
/// The chain backend of a topology as the topology needs it: where the Lightning nodes reach its RPC
/// (<see cref="ITopologyChainEndpoint"/>), and mining, sending and the tip. <see cref="BitcoinCoreTopologyChain"/> (the
/// Bitcoin Core node and the <c>Chain/</c> helpers) is the default; another backend plugs in through
/// <see cref="TopologyBuilder.UseChain(TopologyBuilder.ChainFactory, TopologyBuilder.ChainEndpointFactory?)"/>.
/// </summary>
public interface ITopologyChain : ITopologyChainEndpoint
{
    /// <summary>The deployed chain node.</summary>
    INodeHandle Node { get; }

    /// <summary>The height of the tip.</summary>
    Task<long> GetBlockCountAsync(CancellationToken cancellationToken);

    /// <summary>Mines <paramref name="blocks"/> blocks to the chain node's own wallet and returns their hashes.</summary>
    Task<IReadOnlyList<string>> MineAsync(int blocks, CancellationToken cancellationToken);

    /// <summary>Sends <paramref name="amountSat"/> from the chain node's wallet and returns the txid.</summary>
    Task<string> SendToAddressAsync(string address, long amountSat, CancellationToken cancellationToken);
}