namespace NLightning.Testing.Cluster.Nodes.Cln;

using Images;
using Kube;

/// <summary>
/// How a Core Lightning node of a topology runs: its image, its bitcoind backend and the flags
/// <c>ClnFixture</c> uses today (<c>--developer --dev-bitcoind-poll=1</c>, <c>--ignore-fee-limits=false</c>).
/// </summary>
public sealed record ClnNodeOptions
{
    /// <summary>The image; the version table's CLN (v26.06.8 by digest) by default.</summary>
    public ImageRef Image { get; init; } = ImageVersions.Cln;

    /// <summary>
    /// The bitcoind host CLN's <c>bcli</c> plugin calls: the chain node's alias (its headless Service resolves inside
    /// the run's namespace).
    /// </summary>
    public required string BitcoindHost { get; init; }

    public int BitcoindRpcPort { get; init; } = 18443;

    public required string BitcoindRpcUser { get; init; }

    public required string BitcoindRpcPassword { get; init; }

    /// <summary>The node's alias in gossip; the node name when null.</summary>
    public string? Alias { get; init; }

    /// <summary>CLN's <c>--log-level</c> (the fixture uses <c>debug</c>; the log is the pod's).</summary>
    public string LogLevel { get; init; } = "debug";

    /// <summary>
    /// Turns CLN's feerate checks on (CLN ignores them on regtest by default, which would make any feerate a peer sends
    /// look fine), as <c>ClnFixture</c> does.
    /// </summary>
    public bool EnforceFeeLimits { get; init; } = true;

    /// <summary>More <c>lightningd</c> flags (e.g. <c>--experimental-splicing</c>).</summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];

    /// <summary>The size of the data PVC (<c>/root/.lightning</c>).</summary>
    public string DataSize { get; init; } = "1Gi";

    public WorkloadResources Resources { get; init; } = WorkloadResources.Default;
}