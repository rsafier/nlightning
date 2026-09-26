namespace NLightning.Application.Gossip.Metrics;

/// <summary>
/// The <c>reason</c> tag values of <see cref="GossipMetrics"/> that are not a validator reason (those are the
/// <c>GossipRejectReason</c> names).
/// </summary>
public static class GossipMetricReasons
{
    /// <summary>A channel direction's or node's rate limit.</summary>
    public const string RateLimited = "rate_limited";

    /// <summary>A keep-alive <c>channel_update</c> (same fields) that is not 24 h newer.</summary>
    public const string KeepAliveTooSoon = "keep_alive_too_soon";

    /// <summary>The graph is at <c>MaxChannels</c> or <c>MaxNodes</c>.</summary>
    public const string GraphFull = "graph_full";

    /// <summary>A timestamp too far in the future.</summary>
    public const string FutureTimestamp = "future_timestamp";

    /// <summary>The sending peer is banned.</summary>
    public const string BannedPeer = "banned_peer";

    /// <summary>An invalid signature.</summary>
    public const string InvalidSignature = "invalid_signature";

    /// <summary>The funding output contradicts the announcement (script, amount, transaction index).</summary>
    public const string ChainMismatch = "chain_mismatch";

    /// <summary>The funding output is spent or missing (a closed channel, never the peer's fault).</summary>
    public const string FundingSpent = "funding_spent";

    /// <summary>The funding block cannot be read (pruned node).</summary>
    public const string FundingUnavailable = "funding_unavailable";

    /// <summary>The peer's ingress queue is full.</summary>
    public const string PeerQueueFull = "peer_queue_full";

    /// <summary>The global ingress queue is full.</summary>
    public const string QueueFull = "queue_full";

    /// <summary>A deferred message was retried too often.</summary>
    public const string RetriesExhausted = "retries_exhausted";

    /// <summary>Too many deferred messages wait for a retry.</summary>
    public const string RetryQueueFull = "retry_queue_full";

    /// <summary>The orphan cache is full.</summary>
    public const string OrphanCacheFull = "orphan_cache_full";

    /// <summary>A peer's relay backlog is full: its oldest pending message was dropped.</summary>
    public const string RelayBacklogFull = "relay_backlog_full";

    /// <summary>A reason that is none of the above.</summary>
    public const string Other = "other";
}