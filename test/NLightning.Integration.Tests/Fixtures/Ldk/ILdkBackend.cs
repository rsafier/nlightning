namespace NLightning.Integration.Tests.Fixtures.Ldk;

using Docker.Utils;

/// <summary>
/// What <see cref="LdkFixture"/> needs from where its bitcoind and ldk-server run (<see cref="TestBackend"/>): Docker
/// containers (<see cref="DockerLdkBackend"/>, today's behavior) or a Kubernetes run namespace
/// (<see cref="ClusterLdkBackend"/>, test harness phase 4). Everything else the fixture offers (mining, the waits, LDK's
/// wallet) is written once over these members.
/// </summary>
public interface ILdkBackend : IAsyncDisposable
{
    TestBackendKind Kind { get; }

    /// <summary>The bitcoind the in-process nodes use (RPC and ZMQ at addresses this process reaches).</summary>
    RegtestBitcoinEndpoint Bitcoin { get; }

    /// <summary>The fixture's ldk-server.</summary>
    LdkClient Ldk { get; }

    /// <summary>The host this process dials LDK's p2p port at; it stays the same across <see cref="RestartLdkAsync"/>.</summary>
    string LdkHost { get; }

    /// <summary>LDK's p2p port at <see cref="LdkHost"/>.</summary>
    int LdkPort { get; }

    /// <summary>
    /// The host LDK dials to reach a listener of this process (<c>host.docker.internal</c> for Docker,
    /// <c>host.orb.internal</c> on OrbStack's cluster); the listener binds every interface.
    /// </summary>
    string HostAddressForPeers { get; }

    /// <summary>Starts bitcoind (a <c>miner</c> wallet with mature coins) and ldk-server, and waits until LDK answers.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Restarts ldk-server on the same data and address, and returns once it answers again.</summary>
    Task RestartLdkAsync(CancellationToken cancellationToken);

    /// <summary>Writes the last <paramref name="tail"/> lines of LDK's log to <see cref="Console"/>.</summary>
    Task DumpLdkLogAsync(int tail);
}