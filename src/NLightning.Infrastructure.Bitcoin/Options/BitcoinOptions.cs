namespace NLightning.Infrastructure.Bitcoin.Options;

public class BitcoinOptions
{
    public required string RpcEndpoint { get; set; }
    public required string RpcUser { get; set; }
    public required string RpcPassword { get; set; }
    public required string ZmqHost { get; set; }
    public required int ZmqBlockPort { get; set; }
    public required int ZmqTxPort { get; set; }

    /// <summary>
    /// Subscribes to bitcoind's ZMQ <c>rawtx</c> on <see cref="ZmqTxPort"/> to react to unconfirmed spends of watched
    /// outputs (BOLT 5 plan O8: preimages and revoked commitments seen in the mempool). Blocks alone are enough for
    /// correctness, so this only lowers latency. Default true.
    /// </summary>
    public bool WatchMempool { get; set; } = true;
}