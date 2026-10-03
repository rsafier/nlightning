namespace NLightning.Integration.Tests.Fixtures.Eclair;

using Docker.Utils;

/// <summary>
/// What <see cref="EclairFixture"/> needs from where its bitcoind and Eclair run (<see cref="TestBackend"/>): Docker
/// containers (<see cref="DockerEclairBackend"/>, the behavior before the cluster port) or a Kubernetes run namespace
/// (<see cref="ClusterEclairBackend"/>, test harness phase 4). Mining, the tip waits and Eclair's wallet are written once
/// in the fixture over these members.
/// </summary>
public interface IEclairBackend : IAsyncDisposable
{
    TestBackendKind Kind { get; }

    /// <summary>
    /// The bitcoind the in-process nodes use (RPC scoped to the <c>miner</c> wallet, ZMQ raw block/tx, at addresses
    /// this process reaches). Eclair funds from a wallet of its own on the same bitcoind.
    /// </summary>
    RegtestBitcoinEndpoint Bitcoin { get; }

    /// <summary>Eclair's JSON API.</summary>
    EclairClient Eclair { get; }

    /// <summary>Eclair's node id (hex, lower case).</summary>
    string EclairNodeId { get; }

    /// <summary>The host this process dials Eclair's p2p port at; it stays the same across <see cref="RestartEclairAsync"/>.</summary>
    string EclairHost { get; }

    /// <summary>Eclair's p2p port at <see cref="EclairHost"/>.</summary>
    int EclairPort { get; }

    /// <summary>
    /// The host Eclair dials to reach a listener of this process (<c>host.docker.internal</c> for Docker,
    /// <c>host.orb.internal</c> on OrbStack's cluster); listen on every interface.
    /// </summary>
    string HostAddressForPeers { get; }

    /// <summary>Starts bitcoind (a <c>miner</c> wallet with mature coins) and Eclair (its own wallet), Eclair at the tip.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Restarts Eclair (its data kept) at the same <see cref="EclairHost"/>/<see cref="EclairPort"/> and returns once
    /// it is at the tip.
    /// </summary>
    Task RestartEclairAsync(CancellationToken cancellationToken);

    /// <summary>Writes the last <paramref name="tail"/> lines of Eclair's log to <see cref="Console"/>.</summary>
    Task DumpEclairLogAsync(int tail);
}