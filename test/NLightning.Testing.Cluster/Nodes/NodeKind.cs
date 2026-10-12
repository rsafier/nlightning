namespace NLightning.Testing.Cluster.Nodes;

/// <summary>
/// What a node of a topology runs (plan R7). The value names the <c>nltg.kind</c> label in lower case.
/// </summary>
public enum NodeKind
{
    /// <summary>Anything else (helpers, smoke-test containers).</summary>
    Other,
    BitcoinCore,
    Lnd,
    Cln,
    Eclair,
    Ldk,

    /// <summary>Our daemon as a container (the in-process node has no workload).</summary>
    NLightning,
    Tor,
    Postgres,
    SqlServer
}