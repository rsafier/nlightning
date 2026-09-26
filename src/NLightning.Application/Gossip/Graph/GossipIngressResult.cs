namespace NLightning.Application.Gossip.Graph;

using Domain.Gossip.Validation;

/// <summary>What the ingress did with one gossip message.</summary>
public enum GossipIngressOutcome
{
    /// <summary>Applied to the graph.</summary>
    Accepted,

    /// <summary>Dropped without telling the peer (BOLT 7 "ignore"; an exact duplicate, an old update, ...).</summary>
    Ignored,

    /// <summary>Dropped, and the peer got a <c>warning</c> (and was disconnected when the rule says so).</summary>
    Warned,

    /// <summary>Kept until the channel (or node) it depends on is in the graph.</summary>
    Orphaned,

    /// <summary>The chain could not answer yet (bitcoind behind or down, a reorg, too few confirmations): retry later.</summary>
    Deferred
}

/// <summary>The outcome of the ingress pipeline for one message.</summary>
/// <param name="Outcome">What was done.</param>
/// <param name="Detail">Why, for logs and tests.</param>
/// <param name="RejectReason">The validator's reason, when it decided.</param>
/// <param name="CloseConnection">With <see cref="GossipIngressOutcome.Warned"/>: the connection was closed too.</param>
public sealed record GossipIngressResult(
    GossipIngressOutcome Outcome,
    string Detail,
    GossipRejectReason RejectReason = GossipRejectReason.None,
    bool CloseConnection = false)
{
    /// <summary>
    /// Why the ingress itself refused the message when the validator did not (a rate limit, a graph limit, a banned
    /// peer, an invalid signature, the chain check): a <see cref="Metrics.GossipMetricReasons"/> value, else null.
    /// </summary>
    public string? LimitReason { get; init; }

    /// <summary>The <c>reason</c> tag of the rejected counter (a validator reason in snake_case).</summary>
    public string MetricReason =>
        LimitReason ?? (RejectReason == GossipRejectReason.None ? Metrics.GossipMetricReasons.Other
                                                                 : Metrics.GossipMetrics.TagValue(RejectReason));

    internal static GossipIngressResult Accepted(string detail) => new(GossipIngressOutcome.Accepted, detail);

    internal static GossipIngressResult Limited(string detail, string limitReason) =>
        new(GossipIngressOutcome.Ignored, detail) { LimitReason = limitReason };

    internal static GossipIngressResult Ignored(string detail,
                                                GossipRejectReason reason = GossipRejectReason.None) =>
        new(GossipIngressOutcome.Ignored, detail, reason);

    internal static GossipIngressResult Orphaned(string detail) => new(GossipIngressOutcome.Orphaned, detail);

    internal static GossipIngressResult Deferred(string detail) => new(GossipIngressOutcome.Deferred, detail);
}