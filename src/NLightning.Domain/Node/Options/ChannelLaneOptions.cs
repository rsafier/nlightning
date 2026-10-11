namespace NLightning.Domain.Node.Options;

/// <summary>
/// Per-channel lanes for one peer's channel messages (plan <c>docs/agents/PEER_CHANNEL_PARALLELISM_PLAN.md</c>). Bound
/// from the <c>Node:ChannelLanes</c> configuration section (it is <see cref="NodeOptions.ChannelLanes"/>).
/// </summary>
/// <remarks>
/// With lanes off every connection has one inbound loop: the peer's channel messages are handled one at a time, in
/// arrival order. With lanes on, a connection's dispatcher hands the messages of each established channel to that
/// channel's own lane (FIFO per channel, several channels at once) and everything else (the open flow under temporary
/// ids, unknown or all-zero channel ids) to one serial peer lane. A connection is laned or not for its lifetime: the
/// setting is read when the connection is set up.
/// </remarks>
public class ChannelLaneOptions
{
    /// <summary>The largest <see cref="MaxConcurrentPerPeer"/> accepted.</summary>
    public const int MaxConcurrentPerPeerLimit = 256;

    /// <summary>The largest <see cref="MaxConcurrent"/> accepted.</summary>
    public const int MaxConcurrentLimit = 1024;

    /// <summary>
    /// Whether lanes are on. Unset: the host decides (the daemon turns them on for a server database and leaves them
    /// off on SQLite, whose single writer gains little from them); a host that does not decide leaves them off.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// How many lanes of one connection handle a message at the same time (default 8).
    /// </summary>
    public int MaxConcurrentPerPeer { get; set; } = 8;

    /// <summary>
    /// How many lane items of every connection together are handled at the same time (default 32): each holds a
    /// database context, so keep it well under the database's connection pool.
    /// </summary>
    public int MaxConcurrent { get; set; } = 32;

    /// <summary>
    /// The daemon's choice when <see cref="Enabled"/> is unset, from <c>Database:Provider</c>: on for PostgreSQL, the
    /// production backend whose saves of different channels commit in parallel; off for SQLite (one writer at a time,
    /// owner decision 2026-10-11) and for SQL Server (not measured).
    /// </summary>
    public static bool DefaultFor(string? databaseProvider) =>
        databaseProvider?.Trim().ToLowerInvariant() is "postgres" or "postgresql";

    /// <summary>Whether lanes are on (<see cref="Enabled"/>, off when unset).</summary>
    public bool IsEnabled => Enabled == true;

    /// <summary>
    /// The configuration errors, empty when valid (<see cref="NodeOptions.GetValidationErrors"/>).
    /// </summary>
    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();
        if (MaxConcurrentPerPeer is < 1 or > MaxConcurrentPerPeerLimit)
            errors.Add($"ChannelLanes:{nameof(MaxConcurrentPerPeer)} is {MaxConcurrentPerPeer}; it must be between 1 "
                     + $"and {MaxConcurrentPerPeerLimit}.");
        if (MaxConcurrent is < 1 or > MaxConcurrentLimit)
            errors.Add($"ChannelLanes:{nameof(MaxConcurrent)} is {MaxConcurrent}; it must be between 1 and "
                     + $"{MaxConcurrentLimit}.");
        return errors;
    }
}