namespace NLightning.Integration.Tests.Fixtures.Cln;

/// <summary>
/// Another CLN a test starts next to the fixture's (<see cref="ClnFixture.StartClnAsync"/>), on the fixture's bitcoind:
/// the fixture CLN's image and base flags (<c>--log-level=debug --developer --dev-bitcoind-poll=1</c>, its name as
/// alias), then <c>--ignore-fee-limits=false</c> when <see cref="EnforceFeeLimits"/>, then <see cref="ExtraArgs"/>.
/// </summary>
/// <param name="Name">The container (Docker) or node (cluster) name, also its alias and the host other nodes of the
/// backend dial (<see cref="ExtraClnNode.PeerHost"/>). A DNS-1123 label.</param>
public sealed record ClnNodeSpec(string Name)
{
    /// <summary>More <c>lightningd</c> flags, after the base ones.</summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];

    /// <summary><c>--ignore-fee-limits=false</c>, as the fixture's CLN (CLN ignores them on regtest by default).</summary>
    public bool EnforceFeeLimits { get; init; } = true;

    /// <summary>
    /// Whether this process dials it (its p2p port published on <c>127.0.0.1</c> for Docker); false for a node only other
    /// nodes of the backend dial.
    /// </summary>
    public bool ReachableFromTests { get; init; } = true;

    /// <summary>
    /// Whether the test restarts it (<see cref="ExtraClnNode.RestartAsync"/>): its address must survive the restart (a
    /// fixed host port for Docker; a PVC and a stable ClusterIP Service for the cluster).
    /// </summary>
    public bool Restartable { get; init; }
}