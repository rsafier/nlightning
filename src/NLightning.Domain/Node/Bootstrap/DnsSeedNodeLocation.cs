namespace NLightning.Domain.Node.Bootstrap;

/// <summary>
/// What one BOLT 10 seed answered to a node query (assisted location, NL-541): where one known node listens.
/// </summary>
/// <param name="Seed">The seed root that was queried.</param>
/// <param name="Outcome">How the query went; <see cref="DnsSeedOutcome.Empty"/> is the seed's empty reply, i.e. it
/// does not know the node.</param>
/// <param name="Candidates">The node's validated endpoints: one per address and port. The port comes from the
/// node's SRV answer when it has one, else the default (9735, BOLT 10: the A/AAAA answer is only for nodes on it).</param>
/// <param name="Rejected">The addresses dropped (unroutable, not a family we asked for).</param>
/// <param name="UsedFallbackResolver">True when this answer came from the fallback resolvers
/// (<c>Node:Bootstrap:FallbackNameServers</c>) after the system resolvers gave no endpoint.</param>
/// <param name="SystemResolverOutcome">When the fallback was asked: what the system resolvers answered first.</param>
public sealed record DnsSeedNodeLocation(
    string Seed,
    DnsSeedOutcome Outcome,
    IReadOnlyList<SeedPeerCandidate> Candidates,
    int Rejected = 0,
    bool UsedFallbackResolver = false,
    DnsSeedOutcome? SystemResolverOutcome = null);