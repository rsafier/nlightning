using k8s.Models;
using NBitcoin;

namespace NLightning.Testing.Cluster.Topology;

using Chain;
using Kube;
using Nodes;
using Nodes.BitcoinCore;
using Run;

/// <summary>
/// The chain backend of every topology: the chain lane's <see cref="BitcoinCoreNode"/> (StatefulSet + PVC, ZMQ feeds,
/// a <c>miner</c> wallet loaded at every start) driven through the <see cref="RegtestChain"/> helpers, seen as an
/// <see cref="ITopologyChain"/>. <see cref="TopologyBuilder"/> deploys it by default; <c>LndPairTopology</c> uses it
/// directly. Reorgs, fee seeding and the tx waits are on <see cref="Chain"/>.
/// </summary>
public sealed class BitcoinCoreTopologyChain : ITopologyChain
{
    /// <summary>The blocks mined at start so the wallet's first coinbase is spendable.</summary>
    public const int MaturityBlocks = 101;

    /// <summary>
    /// Where the maturity blocks after the first one pay: a P2WSH of <c>OP_RETURN</c>, an address nobody can spend
    /// from. A coinbase to the wallet costs bitcoind about 40 ms a block (the wallet records and writes it), one to a
    /// foreign address about 1 ms (measured on OrbStack: 100 blocks in 4.2 s against 0.12 s). The wallet's spendable
    /// balance at <see cref="MaturityBlocks"/> is the same either way (only the first coinbase is mature there, 50
    /// BTC); what is gone is the later maturing of blocks 2-101's coinbases, so a long-lived topology that spends more
    /// mines 100 more blocks to the wallet first.
    /// </summary>
    public static string BurnAddress { get; } =
        new Script(OpcodeType.OP_RETURN).WitHash.GetAddress(Network.RegTest).ToString();

    public BitcoinCoreTopologyChain(BitcoinCoreNode bitcoin, RegtestChain chain)
    {
        Bitcoin = bitcoin ?? throw new ArgumentNullException(nameof(bitcoin));
        Chain = chain ?? throw new ArgumentNullException(nameof(chain));
    }

    /// <summary>The deployed bitcoind (addresses, RPC clients, peering).</summary>
    public BitcoinCoreNode Bitcoin { get; }

    /// <summary>The chain helpers over the node's wallet.</summary>
    public RegtestChain Chain { get; }

    public INodeHandle Node => Bitcoin.Handle;

    public string RpcHost => Bitcoin.Name;

    public int RpcPort => BitcoinCorePorts.Rpc;

    public string RpcUser => Bitcoin.Options.RpcUser;

    public string RpcPassword => Bitcoin.Options.RpcPassword;

    public int ZmqRawBlockPort => BitcoinCorePorts.ZmqRawBlock;

    public int ZmqRawTxPort => BitcoinCorePorts.ZmqRawTx;

    public int? ZmqHashBlockPort => BitcoinCorePorts.ZmqHashBlock;

    /// <summary>The startup wait of nodes deployed later (or restarted): one RPC call while bitcoind is up.</summary>
    public V1Container CreateStartupWait() => BitcoinCoreWorkload.StartupWaitContainer(Bitcoin.Options);

    /// <summary>
    /// The bitcoind options of a topology's chain node: its name, image override, extra flags and storage.
    /// </summary>
    public static BitcoinCoreOptions OptionsFor(TopologyNodeSpec chainNode)
    {
        ArgumentNullException.ThrowIfNull(chainNode);
        var options = new BitcoinCoreOptions
        {
            Name = chainNode.Name,
            ExtraArgs = chainNode.Args,
            Storage = chainNode.Storage ?? NodeStorage.Persistent
        };
        return chainNode.Image is { } image ? options with { Image = image } : options;
    }

    /// <summary>
    /// <see cref="TopologyBuilder.ChainEndpointFactory"/>: the endpoint of the chain node <see cref="DeployAsync(TestRun,
    /// TopologyNodeSpec, TimeSpan, CancellationToken)"/> deploys for <paramref name="chainNode"/>.
    /// </summary>
    public static ITopologyChainEndpoint EndpointFor(TopologyNodeSpec chainNode) =>
        new BitcoinCoreChainEndpoint(OptionsFor(chainNode));

    /// <summary>
    /// <see cref="TopologyBuilder.ChainFactory"/>: deploys the topology's chain node with <see cref="OptionsFor"/>.
    /// </summary>
    public static async Task<ITopologyChain> DeployAsync(TestRun run, TopologyNodeSpec chainNode,
                                                         TimeSpan readyTimeout, CancellationToken cancellationToken) =>
        await DeployAsync(run, OptionsFor(chainNode), readyTimeout, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Deploys bitcoind (wallet created or loaded), connects the RPC client (<see cref="RpcRoute.Auto"/>) and mines to
    /// <see cref="MaturityBlocks"/> on a fresh chain: the first block to the wallet, the others to
    /// <see cref="BurnAddress"/>.
    /// </summary>
    public static async Task<BitcoinCoreTopologyChain> DeployAsync(TestRun run, BitcoinCoreOptions options,
                                                                   TimeSpan readyTimeout,
                                                                   CancellationToken cancellationToken,
                                                                   RegtestChainOptions? chainOptions = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Wallet is null)
            throw new ArgumentException("The chain node of a topology needs a wallet (it mines and funds)",
                                        nameof(options));

        var bitcoin = await BitcoinCoreNode.DeployAsync(run, options, readyTimeout, cancellationToken)
                                           .ConfigureAwait(false);
        var rpc = await bitcoin.ConnectRpcAsync(RpcRoute.Auto, cancellationToken).ConfigureAwait(false);
        var chain = new BitcoinCoreTopologyChain(bitcoin, new RegtestChain(rpc, chainOptions));
        var height = await chain.GetBlockCountAsync(cancellationToken).ConfigureAwait(false);
        if (height == 0)
        {
            await chain.MineAsync(1, cancellationToken).ConfigureAwait(false);
            height = 1;
        }

        if (height < MaturityBlocks)
            await chain.Chain.MineAsync(MaturityBlocks - (int)height, cancellationToken, BurnAddress)
                       .ConfigureAwait(false);

        return chain;
    }

    public async Task<long> GetBlockCountAsync(CancellationToken cancellationToken) =>
        (await Chain.GetTipAsync(cancellationToken).ConfigureAwait(false)).Height;

    public Task<IReadOnlyList<string>> MineAsync(int blocks, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blocks);
        return Chain.MineAsync(blocks, cancellationToken);
    }

    public Task<string> SendToAddressAsync(string address, long amountSat, CancellationToken cancellationToken) =>
        Chain.SendAsync(address, amountSat, cancellationToken);

    public override string ToString() => Bitcoin.ToString();
}