namespace NLightning.Application.Gossip.Sync;

using Domain.Protocol.Constants;
using Domain.Protocol.ValueObjects;

/// <summary>
/// The gossip query and sync settings (BOLT 7 plan §3.7, G3-T1/G3-T2, D12), bound from the <c>Gossip</c> section. The
/// property names are the plan's <c>Gossip:*</c> keys.
/// </summary>
public sealed class GossipSyncOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Gossip";

    /// <summary>
    /// The largest BOLT 1 message: a reply must fit in it (type included).
    /// </summary>
    public const int MaxMessageLength = ushort.MaxValue;

    /// <summary>
    /// Whether we query peers for their graph and send them <c>gossip_timestamp_filter</c>s. Unset (the default)
    /// means on everywhere but mainnet (plan D12). Queries from peers are always answered (from an empty graph when
    /// the graph is off).
    /// </summary>
    /// <remarks>Configuration key <c>Gossip:SyncEnabled</c>.</remarks>
    public bool? SyncEnabled { get; set; }

    /// <summary>
    /// Peers offering <c>gossip_queries</c> we run a full range sync with at once (plan: 3); the others only get a
    /// <c>gossip_timestamp_filter</c> for new gossip.
    /// </summary>
    public int SyncPeers { get; set; } = 3;

    /// <summary>
    /// Short channel ids per <c>reply_channel_range</c> (plan: 8,000). Lowered further when timestamps and checksums
    /// are added, so every reply fits in <see cref="MaxMessageLength"/>.
    /// </summary>
    public int MaxScidsPerReply { get; set; } = 8_000;

    /// <summary>
    /// Short channel ids per <c>query_short_channel_ids</c> we send (plan: 8,000). Lowered further when query flags
    /// are added, so the query fits in <see cref="MaxMessageLength"/>.
    /// </summary>
    public int MaxScidsPerQuery { get; set; } = 8_000;

    /// <summary>
    /// How long we wait for the next <c>reply_channel_range</c> or for <c>reply_short_channel_ids_end</c> before the
    /// sync with that peer is given up (no warning: the peer may just be slow).
    /// </summary>
    public TimeSpan SyncReplyTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Every interval, one connected <c>gossip_queries</c> peer (the one synced longest ago) runs the range query again
    /// (historical sync; plan: 20 minutes).
    /// </summary>
    public TimeSpan SyncRotationInterval { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>
    /// How often the short channel ids the ingress dropped (full queues, chain lookups given up; NL-353) are asked for
    /// again from a sync peer.
    /// </summary>
    public TimeSpan MissedScidRetryInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How far back the <c>gossip_timestamp_filter</c> of a synced peer reaches (plan: two weeks, BOLT 7's stale
    /// limit), so updates the range sync could not tell apart are sent again.
    /// </summary>
    public TimeSpan SyncFilterBacklog { get; set; } = TimeSpan.FromSeconds(1_209_600);

    /// <summary>
    /// Queries of one peer waiting for an answer; a peer that sends more before we answered gets a warning and the
    /// query is dropped (BOLT 7: the sender MUST NOT send a query while one is outstanding; the receiver MAY warn).
    /// </summary>
    public int MaxQueuedQueriesPerPeer { get; set; } = 4;

    /// <summary>The effective switch: <see cref="SyncEnabled"/> when set, otherwise true on every chain but mainnet.
    /// </summary>
    public bool IsSyncEnabledFor(BitcoinNetwork network) =>
        SyncEnabled ?? !string.Equals(network.Name, NetworkConstants.Mainnet, StringComparison.OrdinalIgnoreCase);

    /// <summary>The invalid settings, empty when valid.</summary>
    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();
        if (SyncPeers < 0)
            errors.Add($"{nameof(SyncPeers)} must not be negative");
        if (MaxScidsPerReply < 1)
            errors.Add($"{nameof(MaxScidsPerReply)} must be at least 1");
        if (MaxScidsPerQuery < 1)
            errors.Add($"{nameof(MaxScidsPerQuery)} must be at least 1");
        if (SyncReplyTimeout <= TimeSpan.Zero)
            errors.Add($"{nameof(SyncReplyTimeout)} must be positive");
        if (SyncRotationInterval <= TimeSpan.Zero)
            errors.Add($"{nameof(SyncRotationInterval)} must be positive");
        if (MissedScidRetryInterval <= TimeSpan.Zero)
            errors.Add($"{nameof(MissedScidRetryInterval)} must be positive");
        if (SyncFilterBacklog < TimeSpan.Zero)
            errors.Add($"{nameof(SyncFilterBacklog)} must not be negative");
        if (MaxQueuedQueriesPerPeer < 1)
            errors.Add($"{nameof(MaxQueuedQueriesPerPeer)} must be at least 1");
        return errors;
    }
}