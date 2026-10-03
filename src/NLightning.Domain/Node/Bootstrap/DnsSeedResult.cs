namespace NLightning.Domain.Node.Bootstrap;

/// <summary>
/// What one BOLT 10 DNS seed answered.
/// </summary>
/// <param name="Seed">The seed root that was queried.</param>
/// <param name="Outcome">How the query went.</param>
/// <param name="Candidates">The validated peers (node id, routable address, SRV port); may be non-empty with an outcome
/// other than <see cref="DnsSeedOutcome.Ok"/> when the seed timed out part way.</param>
/// <param name="Rejected">The records or addresses dropped (bad node id, unusable address, wrong family).</param>
/// <param name="UsedFallbackResolver">True when this answer came from the fallback resolvers
/// (<c>Node:Bootstrap:FallbackNameServers</c>) after the system resolvers gave no candidate.</param>
/// <param name="SystemResolverOutcome">When the fallback was asked: what the system resolvers answered first.</param>
public sealed record DnsSeedResult(
    string Seed,
    DnsSeedOutcome Outcome,
    IReadOnlyList<SeedPeerCandidate> Candidates,
    int Rejected,
    bool UsedFallbackResolver = false,
    DnsSeedOutcome? SystemResolverOutcome = null);

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