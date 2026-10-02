using NBitcoin.RPC;

namespace NLightning.Integration.Tests.Cluster;

using Docker.Utils;
using Testing.Cluster.Nodes;
using Testing.Cluster.Run;
using Testing.Cluster.Topology;

/// <summary>
/// The topology's bitcoind as an in-process node needs it (<see cref="RegtestBitcoinEndpoint"/>): its RPC with the
/// chain node's credentials and wallet, and its ZMQ raw block / raw tx feeds, all at an address the test process
/// reaches.
/// </summary>
/// <remarks>
/// <see cref="ITopologyChain.RpcHost"/> is the bare alias, which resolves only inside the run's namespace. From the
/// host (OrbStack) the chain is reached by its <b>pod IP</b> (spike check 1: 2 ms, no DNS); inside the cluster (the
/// in-cluster runner, <c>InClusterTestRunner</c>) by its headless Service name. The address is read once: the
/// endpoint does not follow a bitcoind restart (a test that restarts bitcoind rebuilds the in-process node).
/// </remarks>
public static class ClusterChainEndpoint
{
    /// <summary>The bitcoind wallet the RPC client calls (the topology chain's mining wallet).</summary>
    public const string DefaultWallet = "miner";

    /// <summary>
    /// The endpoint of <paramref name="chain"/> for a node in this process (<see cref="KubeClientFactory.DetectSource"/>
    /// decides the placement).
    /// </summary>
    public static RegtestBitcoinEndpoint Create(ITopologyChain chain, string? wallet = DefaultWallet) =>
        Create(chain, KubeClientFactory.DetectSource() == KubeConfigSource.InCluster, wallet);

    /// <summary>The endpoint of <paramref name="chain"/> for a process in the cluster or on the host.</summary>
    public static RegtestBitcoinEndpoint Create(ITopologyChain chain, bool inCluster, string? wallet = DefaultWallet)
    {
        ArgumentNullException.ThrowIfNull(chain);
        var host = HostFor(chain.Node, inCluster);
        var rpc = new RPCClient($"{chain.RpcUser}:{chain.RpcPassword}", $"http://{host}:{chain.RpcPort}",
                                NBitcoin.Network.RegTest);
        return new RegtestBitcoinEndpoint(wallet is null ? rpc : rpc.SetWalletContext(wallet), host,
                                          chain.ZmqRawBlockPort, chain.ZmqRawTxPort);
    }

    /// <summary>
    /// The address of <paramref name="chainNode"/> for this process: the headless Service name in the cluster, the pod
    /// IP on the host.
    /// </summary>
    /// <exception cref="InvalidOperationException">On the host, the pod has no IP yet.</exception>
    public static string HostFor(INodeHandle chainNode, bool inCluster)
    {
        ArgumentNullException.ThrowIfNull(chainNode);
        if (inCluster)
            return chainNode.ServiceDnsName;

        return chainNode.PodIp
            ?? throw new InvalidOperationException($"{chainNode.Name} has no pod IP yet (wait until it is ready)");
    }
}