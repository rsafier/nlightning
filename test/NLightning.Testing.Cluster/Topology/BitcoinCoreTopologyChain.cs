namespace NLightning.Testing.Cluster.Topology;

using Chain;
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

    /// <summary>The bitcoind options of a topology's chain node: its name, image override and extra flags.</summary>
    public static BitcoinCoreOptions OptionsFor(TopologyNodeSpec chainNode)
    {
        ArgumentNullException.ThrowIfNull(chainNode);
        var options = new BitcoinCoreOptions { Name = chainNode.Name, ExtraArgs = chainNode.Args };
        return chainNode.Image is { } image ? options with { Image = image } : options;
    }

    /// <summary>
    /// <see cref="TopologyBuilder.ChainFactory"/>: deploys the topology's chain node with <see cref="OptionsFor"/>.
    /// </summary>
    public static async Task<ITopologyChain> DeployAsync(TestRun run, TopologyNodeSpec chainNode,
                                                         TimeSpan readyTimeout, CancellationToken cancellationToken) =>
        await DeployAsync(run, OptionsFor(chainNode), readyTimeout, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Deploys bitcoind (wallet created or loaded), connects the RPC client (<see cref="RpcRoute.Auto"/>) and mines to
    /// <see cref="MaturityBlocks"/> on a fresh chain.
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
        if (height < MaturityBlocks)
            await chain.MineAsync(MaturityBlocks - (int)height, cancellationToken).ConfigureAwait(false);

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