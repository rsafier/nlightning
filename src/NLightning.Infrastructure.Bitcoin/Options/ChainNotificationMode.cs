namespace NLightning.Infrastructure.Bitcoin.Options;

/// <summary>
/// How the chain monitor learns about new blocks (<c>Bitcoin:Notifications</c>, NL-1094).
/// </summary>
public enum ChainNotificationMode
{
    /// <summary>
    /// bitcoind's ZMQ <c>rawblock</c> (and <c>rawtx</c> for the mempool), with the RPC tip poll as a safety net. The
    /// default.
    /// </summary>
    Zmq = 0,

    /// <summary>
    /// RPC only: the monitor reads the tip every <see cref="BitcoinOptions.PollInterval"/> and processes every new block
    /// in order, for a node without ZMQ (rbitcoin, a remote bitcoind behind an RPC-only proxy). The mempool is watched by
    /// polling <c>gettxspendingprevout</c> only when <see cref="BitcoinOptions.WatchMempool"/> is set.
    /// </summary>
    Poll = 1
}