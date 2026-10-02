namespace NLightning.Testing.Cluster.Topology;

using Nodes;

/// <summary>
/// The chain backend of a topology as the topology needs it: where the Lightning nodes reach its RPC, and mining,
/// sending and the tip. <see cref="BitcoinCoreTopologyChain"/> (the Bitcoin Core node and the <c>Chain/</c> helpers) is
/// the default; another backend plugs in through <see cref="TopologyBuilder.UseChain"/>.
/// </summary>
public interface ITopologyChain
{
    /// <summary>The deployed chain node.</summary>
    INodeHandle Node { get; }

    /// <summary>The host the Lightning nodes call (the chain node's alias, resolved inside the run's namespace).</summary>
    string RpcHost { get; }

    int RpcPort { get; }

    string RpcUser { get; }

    string RpcPassword { get; }

    /// <summary>bitcoind's <c>zmqpubrawblock</c> port on <see cref="RpcHost"/> (LND follows the chain over ZMQ).</summary>
    int ZmqRawBlockPort { get; }

    /// <summary>bitcoind's <c>zmqpubrawtx</c> port on <see cref="RpcHost"/>.</summary>
    int ZmqRawTxPort { get; }

    /// <summary>The height of the tip.</summary>
    Task<long> GetBlockCountAsync(CancellationToken cancellationToken);

    /// <summary>Mines <paramref name="blocks"/> blocks to the chain node's own wallet and returns their hashes.</summary>
    Task<IReadOnlyList<string>> MineAsync(int blocks, CancellationToken cancellationToken);

    /// <summary>Sends <paramref name="amountSat"/> from the chain node's wallet and returns the txid.</summary>
    Task<string> SendToAddressAsync(string address, long amountSat, CancellationToken cancellationToken);
}