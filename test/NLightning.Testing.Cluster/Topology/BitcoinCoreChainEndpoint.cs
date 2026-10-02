using k8s.Models;

namespace NLightning.Testing.Cluster.Topology;

using Nodes.BitcoinCore;

/// <summary>
/// The <see cref="ITopologyChainEndpoint"/> of a <see cref="BitcoinCoreTopologyChain"/> before it is deployed: the
/// alias, ports and credentials of <see cref="Options"/>, and bitcoind's own image as the nodes' startup wait
/// (<see cref="BitcoinCoreWorkload.StartupWaitContainer"/>).
/// </summary>
public sealed class BitcoinCoreChainEndpoint(BitcoinCoreOptions options) : ITopologyChainEndpoint
{
    public BitcoinCoreOptions Options { get; } = options ?? throw new ArgumentNullException(nameof(options));

    public string RpcHost => Options.Name;

    public int RpcPort => BitcoinCorePorts.Rpc;

    public string RpcUser => Options.RpcUser;

    public string RpcPassword => Options.RpcPassword;

    public int ZmqRawBlockPort => BitcoinCorePorts.ZmqRawBlock;

    public int ZmqRawTxPort => BitcoinCorePorts.ZmqRawTx;

    public V1Container CreateStartupWait() => BitcoinCoreWorkload.StartupWaitContainer(Options);

    public override string ToString() => $"{RpcHost}:{RpcPort}";
}