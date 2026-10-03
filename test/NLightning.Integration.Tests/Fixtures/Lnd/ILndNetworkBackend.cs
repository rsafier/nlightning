using NBitcoin.RPC;
using NLightning.Testing.Lnd;

namespace NLightning.Integration.Tests.Fixtures.Lnd;

/// <summary>
/// What <see cref="LightningRegtestNetworkFixture"/> needs from where its bitcoind and its LND nodes run
/// (<see cref="TestBackend"/>), shaped like the members the LND Docker tests call on the fixture: <see cref="Bitcoin"/>,
/// <see cref="BitcoinZmqPorts"/>, <see cref="LndNodes"/>, <see cref="GetLndNode"/> and <see cref="RestartLndAsync"/>
/// (test harness phase 3), plus where our in-process nodes and the LND nodes reach each other
/// (<see cref="GetLndPeerEndpointAsync"/>, <see cref="HostAddressForPeers"/>) and the LND logs of a failed test. The
/// Docker backend is LNUnit's orchestration (<see cref="DockerLndBackend"/>); the cluster backend is
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

    /// <summary>
    /// The host an LND node dials to reach a listener of this process (<c>HOST_ADDRESS</c> or
    /// <c>host.docker.internal</c> for Docker, <c>host.orb.internal</c> for OrbStack's cluster).
    /// </summary>
    string HostAddressForPeers { get; }

    /// <summary>The LND node <paramref name="alias"/>; the same connection object across restarts.</summary>
    LndNodeConnection GetLndNode(string alias);

    /// <summary>
    /// The <c>host:port</c> of <paramref name="lnd"/>'s p2p listener that an in-process node of this process dials and
    /// stores: the container's IP on Docker, the Service's DNS name on the cluster (it follows the pod across a
    /// restart, a pod IP does not, NL-780).
    /// </summary>
    Task<string> GetLndPeerEndpointAsync(LndNodeConnection lnd, CancellationToken cancellationToken);

    /// <summary>
    /// Restarts the LND node <paramref name="alias"/> on its data (same node id, wallet and channels). Docker: returns
    /// once the container restarted (the caller waits for LND to be ready); cluster: once it is <c>SERVER_ACTIVE</c>
    /// and synced, its network peers dialled it again and its channels with them are active again.
    /// </summary>
    Task RestartLndAsync(string alias);

    /// <summary>Writes the last <paramref name="tail"/> log lines of the LND nodes <paramref name="aliases"/> to <see cref="Console"/>.</summary>
    Task DumpLndLogsAsync(IEnumerable<string> aliases, int tail);

    /// <summary>Starts bitcoind and the LND nodes with their channels, and returns once they are usable.</summary>
    Task StartAsync(CancellationToken cancellationToken);
}