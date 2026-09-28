namespace NLightning.Application.Gossip.Sync;

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
    /// means on everywhere, mainnet included (plan D12, opened in wave d12). Queries from peers are always answered (from an empty graph when
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
    /// How far back the <c>gossip_timestamp_filter</c> of a peer synced without <c>gossip_queries_ex</c> reaches (plan:
    /// two weeks, BOLT 7's stale limit), so updates the range sync could not tell apart are sent again. After a sync
    /// with timestamps the filter starts where the sync started (the sync already asked for every newer update).
    /// </summary>
    public TimeSpan SyncFilterBacklog { get; set; } = TimeSpan.FromSeconds(1_209_600);

    /// <summary>
    /// A channel we do not know is not asked for when the peer's <c>reply_channel_range</c> timestamps (with
    /// <c>gossip_queries_ex</c>) say both of its updates are older than this, or missing (BOLT 7's two weeks, the same
    /// limit as <c>Gossip:StaleAfter</c>): the ingress would ignore those updates as stale and keep a channel nobody
    /// can route through (LND's zombie rule; on mainnet about a quarter of what the peers list are abandoned channels
    /// whose funding output is unspent but whose nodes stopped updating them, NL-404). It comes with a later sync or
    /// the live gossip once one of its nodes updates it. Zero asks for every channel.
    /// </summary>
    /// <remarks>Configuration key <c>Gossip:SkipChannelsStaleFor</c>.</remarks>
    public TimeSpan SkipChannelsStaleFor { get; set; } = TimeSpan.FromSeconds(1_209_600);

    /// <summary>
    /// Queries of one peer waiting for an answer; a peer that sends more before we answered gets a warning and the
    /// query is dropped (BOLT 7: the sender MUST NOT send a query while one is outstanding; the receiver MAY warn).
    /// </summary>
    public int MaxQueuedQueriesPerPeer { get; set; } = 4;

    /// <summary>
    /// How long an unknown channel one sync peer was asked for is not asked from another (NL-415), counted from that
    /// peer's <c>reply_short_channel_ids_end</c>: its announcement is queued in the ingress or waits for its chain
    /// lookup meanwhile. A failed query, or an end with <c>full_information</c> = 0, frees its channels at once, and
    /// what the ingress drops is asked for again by the missed-channel retry regardless; a channel still missing from
    /// the graph when its claim ends goes to that retry too (once). Zero turns it off (every sync peer's re-diff sees
    /// only the graph, NL-402).
    /// </summary>
    /// <remarks>Configuration key <c>Gossip:QueriedChannelTtl</c>.</remarks>
    public TimeSpan QueriedChannelTtl { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The default of <see cref="MaxOutboxGossipPerPeer"/>.</summary>
    public const int DefaultMaxOutboxGossipPerPeer = 10_000;

    /// <summary>The default of <see cref="MaxOutboxGossipBytesPerPeer"/> (4 MiB).</summary>
    public const long DefaultMaxOutboxGossipBytesPerPeer = 4L * 1024 * 1024;

    /// <summary>
    /// Gossip messages (our own and relayed) one peer's <c>PeerOutbox</c> holds unsent at most (NL-360); one more is
    /// refused (counted in <c>nlightning.gossip.outbox.refused</c>) while channel messages are always queued, and the
    /// relay pauses that connection until its outbox drained to half (<c>GossipRelayOptions.RelayResumePercent</c>),
    /// keeping its place in the backlog and its pending messages. Zero means no cap.
    /// </summary>
    /// <remarks>
    /// Configuration key <c>Gossip:MaxOutboxGossipPerPeer</c>, default <see cref="DefaultMaxOutboxGossipPerPeer"/>
    /// (10,000: ten seconds of the backlog pace, about 3 MB of typical gossip; mainnet's graph backlog is about
    /// 110,000 messages). The <c>outbox_gossip</c> queue gauge is reported either way.
    /// </remarks>
    public int MaxOutboxGossipPerPeer { get; set; } = DefaultMaxOutboxGossipPerPeer;

    /// <summary>
    /// The wire bytes of gossip one peer's <c>PeerOutbox</c> holds unsent at most (NL-360), next to
    /// <see cref="MaxOutboxGossipPerPeer"/>: it bounds a queue of large <c>node_announcement</c>s (up to 64 KiB each).
    /// An empty gossip share always takes one message. Zero means no byte cap.
    /// </summary>
    /// <remarks>Configuration key <c>Gossip:MaxOutboxGossipBytesPerPeer</c>, default 4 MiB.</remarks>
    public long MaxOutboxGossipBytesPerPeer { get; set; } = DefaultMaxOutboxGossipBytesPerPeer;

    /// <summary>The effective switch: <see cref="SyncEnabled"/> when set, otherwise true on every chain (D12).</summary>
    public bool IsSyncEnabledFor(BitcoinNetwork network)
    {
        // On every network since D12; the callers keep asking per network so a chain can be gated again
        _ = network;
        return SyncEnabled ?? true;
    }

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
        if (SkipChannelsStaleFor < TimeSpan.Zero)
            errors.Add($"{nameof(SkipChannelsStaleFor)} must not be negative");
        if (MaxQueuedQueriesPerPeer < 1)
            errors.Add($"{nameof(MaxQueuedQueriesPerPeer)} must be at least 1");
        if (QueriedChannelTtl < TimeSpan.Zero)
            errors.Add($"{nameof(QueriedChannelTtl)} must not be negative");
        if (MaxOutboxGossipPerPeer < 0)
            errors.Add($"{nameof(MaxOutboxGossipPerPeer)} must not be negative");
        if (MaxOutboxGossipBytesPerPeer < 0)
            errors.Add($"{nameof(MaxOutboxGossipBytesPerPeer)} must not be negative");
        return errors;
    }
}