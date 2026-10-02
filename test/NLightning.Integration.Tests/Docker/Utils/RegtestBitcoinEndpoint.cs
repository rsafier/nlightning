using NBitcoin.RPC;

namespace NLightning.Integration.Tests.Docker.Utils;

using Fixtures;

/// <summary>
/// The bitcoind an <see cref="NLightningTestNode"/> talks to: its RPC client (which must carry user/password
/// credentials, the node's configuration is built from them) and where its ZMQ raw block and raw tx feeds are.
/// </summary>
/// <remarks>
/// The shared regtest network's miner is <see cref="FromFixture"/>; a test with its own bitcoind (the CLN interop
/// tests, <c>Fixtures/ClnFixture</c>) builds one directly.
/// </remarks>
public sealed record RegtestBitcoinEndpoint(RPCClient Rpc, string ZmqHost, int ZmqBlockPort, int ZmqTxPort)
{
    /// <summary>
    /// How long after each start an <see cref="NLightningTestNode"/> on this endpoint hands its chain monitor a block
    /// that ZMQ lost (<see cref="NLightningTestNode.ZmqStartupGuardLag"/>); null (Docker) for none. A ZMQ subscriber
    /// gets only what is published after its subscription reached the publisher, so a block mined right after the
    /// node's start is lost when the connection is slow to set up, and the monitor, which follows ZMQ, would see
    /// nothing until the next block. The cluster backend sets it (<c>ClusterChainEndpoint</c>): its ZMQ path to
    /// bitcoind's pod is slower to connect than Docker's published port on 127.0.0.1.
    /// </summary>
    public TimeSpan? ZmqStartupGuard { get; init; }

    /// <summary>
    /// The miner of the shared regtest network: LNUnit's RPC client, and the ZMQ ports from the miner's command line
    /// on the same host.
    /// </summary>
    public static RegtestBitcoinEndpoint FromFixture(LightningRegtestNetworkFixture fixture)
    {
        Assert.NotNull(fixture.Builder);
        var bitcoinConfiguration = fixture.Builder.Configuration.BTCNodes[0];
        var zmqRawBlockPort = bitcoinConfiguration.Cmd.First(c => c.Contains("-zmqpubrawblock")).Split(':')[2];
        var zmqRawTxPort = bitcoinConfiguration.Cmd.First(c => c.Contains("-zmqpubrawtx")).Split(':')[2];
        var rpc = fixture.Bitcoin;
        return new RegtestBitcoinEndpoint(rpc, rpc.Address.Host, int.Parse(zmqRawBlockPort),
                                          int.Parse(zmqRawTxPort));
    }
}