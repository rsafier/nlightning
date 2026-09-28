namespace NLightning.Domain.Node.Bootstrap;

/// <summary>
/// What one BOLT 10 DNS seed answered.
/// </summary>
/// <param name="Seed">The seed root that was queried.</param>
/// <param name="Outcome">How the query went.</param>
/// <param name="Candidates">The validated peers (node id, routable address, SRV port); may be non-empty with an outcome
/// other than <see cref="DnsSeedOutcome.Ok"/> when the seed timed out part way.</param>
/// <param name="Rejected">The records or addresses dropped (bad node id, unusable address, wrong family).</param>
public sealed record DnsSeedResult(
    string Seed,
    DnsSeedOutcome Outcome,
    IReadOnlyList<SeedPeerCandidate> Candidates,
    int Rejected);

/// <summary>
/// The outcome of a BOLT 10 DNS seed query.
/// </summary>
public enum DnsSeedOutcome
{
    /// <summary>The seed answered SRV records.</summary>
    Ok,

    /// <summary>The seed answered without records.</summary>
    Empty,

    /// <summary>The seed name does not exist (NXDOMAIN).</summary>
    NxDomain,

    /// <summary>The resolver failed or refused (SERVFAIL, REFUSED).</summary>
    ServerFailure,

    /// <summary>The seed did not answer in time.</summary>
    Timeout,

    /// <summary>Any other failure.</summary>
    Error
}