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
    /// The miner of the shared regtest network: the fixture's RPC client, and its ZMQ ports on the same host.
    /// </summary>
    public static RegtestBitcoinEndpoint FromFixture(LightningRegtestNetworkFixture fixture)
    {
        var (zmqRawBlockPort, zmqRawTxPort) = fixture.BitcoinZmqPorts;
        var rpc = fixture.Bitcoin;
        return new RegtestBitcoinEndpoint(rpc, rpc.Address.Host, zmqRawBlockPort, zmqRawTxPort);
    }
}