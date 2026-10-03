using NBitcoin.RPC;
using NLightning.Testing.Lnd;

namespace NLightning.Integration.Tests.Fixtures.Lnd;

/// <summary>
/// What <see cref="LightningRegtestNetworkFixture"/> needs from where its bitcoind and its LND nodes run
/// (<see cref="TestBackend"/>), shaped like the members the LND Docker tests call on the fixture: <see cref="Bitcoin"/>,
/// <see cref="BitcoinZmqPorts"/>, <see cref="LndNodes"/>, <see cref="GetLndNode"/> and <see cref="RestartLndAsync"/>
/// (test harness phase 3). The Docker backend is <see cref="DockerLndBackend"/> (LNUnit); the cluster backend is
/// <see cref="ClusterLndBackend"/>. The fixture's other members (<c>GetOrCreateAsync</c>, the alias and image
/// constants) stay on the fixture.
/// </summary>
public interface ILndNetworkBackend : IAsyncDisposable
{
    TestBackendKind Kind { get; }

    /// <summary>The miner's RPC (its wallet), at an address this process reaches.</summary>
    RPCClient Bitcoin { get; }

    /// <summary>The ZMQ ports of the miner's raw block and raw tx feeds, on <see cref="Bitcoin"/>'s host.</summary>
    (int RawBlockPort, int RawTxPort) BitcoinZmqPorts { get; }

    /// <summary>The LND nodes (alice, bob, carol, david), ready and in that order.</summary>
    IReadOnlyList<LndNodeConnection> LndNodes { get; }

    /// <summary>The LND node <paramref name="alias"/>; the same connection object across restarts.</summary>
    LndNodeConnection GetLndNode(string alias);

    /// <summary>
    /// Restarts the LND node <paramref name="alias"/> on its data (same node id, wallet and channels) and returns once
    /// it serves every RPC again.
    /// </summary>
    Task RestartLndAsync(string alias);

    /// <summary>Starts bitcoind and the LND nodes with their channels, and returns once they are usable.</summary>
    Task StartAsync(CancellationToken cancellationToken);
}