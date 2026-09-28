namespace NLightning.Domain.Node.Bootstrap;

/// <summary>
/// What the BOLT 10 bootstrap has done in this process (NL-113): its runs, every seed query and every dial, oldest
/// first. A snapshot; the lists are bounded (<see cref="MaxRecords"/> each, the oldest dropped).
/// </summary>
/// <param name="Enabled">Whether the bootstrap is on for the node's network.</param>
/// <param name="StartedAt">When the loop started, null when it never did.</param>
/// <param name="FinishedAt">When the loop ended, null while it runs or when it never started.</param>
/// <param name="EndReason">Why the loop ended, null while it runs.</param>
/// <param name="Runs">One record per run, skipped runs included.</param>
/// <param name="SeedQueries">One record per seed query.</param>
/// <param name="Dials">One record per dial.</param>
public sealed record PeerBootstrapStatus(
    bool Enabled,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? EndReason,
    IReadOnlyList<BootstrapRunRecord> Runs,
    IReadOnlyList<BootstrapSeedQueryRecord> SeedQueries,
    IReadOnlyList<BootstrapDialRecord> Dials)
{
    /// <summary>The most records kept per list.</summary>
    public const int MaxRecords = 1000;

    /// <summary>The status of a bootstrap that never started.</summary>
    public static PeerBootstrapStatus NotStarted(bool enabled) => new(enabled, null, null, null, [], [], []);
}

/// <summary>One bootstrap run.</summary>
/// <param name="Run">The run's number, from 1.</param>
/// <param name="StartedAt">When it started.</param>
/// <param name="SkipReason">Why it was skipped (the gate), null when it ran.</param>
/// <param name="Collected">The candidates the seeds returned.</param>
/// <param name="Selected">The candidates left to dial after the dedupe.</param>
/// <param name="Attempted">The dials made.</param>
/// <param name="Connected">The dials that connected.</param>
/// <param name="PeersAfter">The connected peers when the run ended.</param>
/// <remarks>
/// <paramref name="Attempted"/> and <paramref name="Connected"/> count the graph top-up's dials too (NL-543);
/// <paramref name="Collected"/> and <paramref name="Selected"/> are the seeds' only.
/// </remarks>
public sealed record BootstrapRunRecord(
    int Run,
    DateTimeOffset StartedAt,
    string? SkipReason,
    int Collected,
    int Selected,
    int Attempted,
    int Connected,
    int PeersAfter)
{
    /// <summary>The graph nodes selected for the top-up (NL-543).</summary>
    public int GraphSelected { get; init; }

    /// <summary>The graph nodes dialed.</summary>
    public int GraphAttempted { get; init; }

    /// <summary>The graph nodes that connected.</summary>
    public int GraphConnected { get; init; }

    /// <summary>True when the run asked the DNS seeds (the graph top-up did not reach <c>MinPeers</c>).</summary>
    public bool AskedSeeds { get; init; }
}

/// <summary>One seed query of a run.</summary>
/// <param name="Run">The run it belongs to.</param>
/// <param name="At">When it ended.</param>
/// <param name="Seed">The seed root.</param>
/// <param name="Outcome">The seed's outcome (the fallback's when it was used).</param>
/// <param name="Candidates">The validated candidates.</param>
/// <param name="Rejected">The records or addresses dropped.</param>
/// <param name="UsedFallbackResolver">True when the fallback resolvers answered.</param>
/// <param name="SystemResolverOutcome">What the system resolvers answered, when the fallback was asked.</param>
/// <param name="Elapsed">The time the query took, the fallback included.</param>
/// <param name="Error">The exception's message when the query threw.</param>
public sealed record BootstrapSeedQueryRecord(
    int Run,
    DateTimeOffset At,
    string Seed,
    DnsSeedOutcome Outcome,
    int Candidates,
    int Rejected,
    bool UsedFallbackResolver,
    DnsSeedOutcome? SystemResolverOutcome,
    TimeSpan Elapsed,
    string? Error);

/// <summary>One dial of a run.</summary>
/// <param name="Run">The run it belongs to.</param>
/// <param name="At">When it ended.</param>
/// <param name="Candidate">The peer dialed.</param>
/// <param name="Outcome">How it went.</param>
/// <param name="Elapsed">The time it took.</param>
/// <param name="Error">The failure, null when it connected.</param>
public sealed record BootstrapDialRecord(
    int Run,
    DateTimeOffset At,
    SeedPeerCandidate Candidate,
    BootstrapDialOutcome Outcome,
    TimeSpan Elapsed,
    string? Error);

/// <summary>How a bootstrap dial went.</summary>
public enum BootstrapDialOutcome
{
    /// <summary>Connected (TCP, BOLT 8 handshake and init).</summary>
    Connected,

    /// <summary>The peer was already connected.</summary>
    AlreadyConnected,

    /// <summary>The connection failed (refused, unreachable, handshake or init failure).</summary>
    Failed,

    /// <summary><c>Node:Bootstrap:ConnectTimeout</c> ran out and the dial was cancelled.</summary>
    TimedOut
}