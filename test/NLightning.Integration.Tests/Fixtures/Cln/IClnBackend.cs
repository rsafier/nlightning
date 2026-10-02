namespace NLightning.Integration.Tests.Fixtures.Cln;

using Docker.Utils;

/// <summary>
/// What <see cref="ClnFixture"/> needs from where its bitcoind and CLN run (<see cref="TestBackend"/>): Docker
/// containers (<see cref="DockerClnBackend"/>, today's behavior) or a Kubernetes run namespace
/// (<see cref="ClusterClnBackend"/>). Everything else the fixture offers (mining, waits, CLN's wallet) is written once
/// over these members.
/// </summary>
public interface IClnBackend : IAsyncDisposable
{
    TestBackendKind Kind { get; }

    /// <summary>The bitcoind the in-process nodes use (RPC and ZMQ at addresses this process reaches).</summary>
    RegtestBitcoinEndpoint Bitcoin { get; }

    /// <summary>The fixture's CLN.</summary>
    ClnClient Cln { get; }

    /// <summary>CLN's node id (hex, lower case).</summary>
    string ClnNodeId { get; }

    /// <summary>The host this process dials CLN's p2p port at.</summary>
    string ClnHost { get; }

    /// <summary>CLN's p2p port at <see cref="ClnHost"/>.</summary>
    int ClnPort { get; }

    /// <summary>
    /// The host CLN (and every other node the backend runs) dials to reach a listener of this process
    /// (<c>host.docker.internal</c> for Docker, <c>host.orb.internal</c> for OrbStack's cluster). The listener must
    /// bind every interface (Docker) or loopback (OrbStack's cluster) as the backend needs; all interfaces works for
    /// both.
    /// </summary>
    string HostAddressForPeers { get; }

    /// <summary>Starts bitcoind (a <c>miner</c> wallet with mature coins) and CLN, and waits until CLN is at the tip.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Starts another CLN on the same bitcoind (and network), configured by <paramref name="spec"/>; the caller disposes
    /// it, which removes it.
    /// </summary>
    Task<ExtraClnNode> StartClnAsync(ClnNodeSpec spec, CancellationToken cancellationToken);

    /// <summary>Writes the last <paramref name="tail"/> lines of CLN's log to <see cref="Console"/>.</summary>
    Task DumpClnLogAsync(int tail);
}