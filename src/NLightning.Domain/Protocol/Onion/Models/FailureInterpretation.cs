namespace NLightning.Domain.Protocol.Onion.Models;

using Enums;

/// <summary>
/// What the origin node should do with a failed payment attempt, following BOLT 4 "Receiving Failure Codes". Built by
/// <see cref="Interpreters.FailureInterpreter"/>.
/// </summary>
/// <remarks>
/// Route positions are indexes into the route's hops (0 = the first hop, our peer). The channel used to reach hop
/// <c>k</c> is "the incoming channel of hop <c>k</c>", so the outgoing channel of an erring intermediate hop <c>i</c>
/// is the incoming channel of hop <c>i + 1</c>.
/// </remarks>
public sealed class FailureInterpretation
{
    /// <summary>
    /// The index of the hop that sent the failure, or <c>null</c> when no hop's HMAC matched (the failure cannot be
    /// attributed).
    /// </summary>
    public int? ErringHopIndex { get; init; }

    /// <summary>
    /// True when the final node sent the failure.
    /// </summary>
    public bool IsFinalNode { get; init; }

    /// <summary>
    /// The failure code, if one could be read.
    /// </summary>
    public FailureCode? Code { get; init; }

    /// <summary>
    /// The parsed failure message, if it could be parsed.
    /// </summary>
    public FailureMessage? Message { get; init; }

    /// <summary>
    /// True when the PERM bit is set.
    /// </summary>
    public bool IsPermanent { get; init; }

    /// <summary>
    /// True when the failure is attributed to a node rather than a channel (the NODE bit, or an unreadable failure
    /// from an intermediate hop).
    /// </summary>
    public bool IsNodeFailure { get; init; }

    /// <summary>
    /// <c>false</c> when the payment should be failed: the final node returned a PERM failure, or one this node
    /// does not understand. Otherwise the origin SHOULD (intermediate hop) or MAY (final node) retry.
    /// </summary>
    public bool ShouldRetry { get; init; }

    /// <summary>
    /// The hop whose channels SHOULD all be removed from consideration (NODE failure from an intermediate hop). When
    /// <see cref="IsPermanent"/> is false they SHOULD be restored as new <c>channel_update</c>s arrive from peers.
    /// </summary>
    public int? ExcludedNodeHopIndex { get; init; }

    /// <summary>
    /// The hop whose incoming channel failed, i.e. <see cref="ErringHopIndex"/> + 1: the outgoing channel of an
    /// intermediate hop that returned a channel failure. Removed for good when <see cref="IsPermanent"/>, otherwise
    /// only until the channel is updated.
    /// </summary>
    public int? FailedChannelHopIndex { get; init; }

    /// <summary>
    /// The <c>channel_update</c> payload (without the message type) of an UPDATE failure from an intermediate hop,
    /// when one was sent. BOLT 4: the origin MAY use it, if valid and newer than the one it routed with, to retry
    /// this payment, and MUST NOT expose it anywhere else (not applied to the graph, not relayed as gossip).
    /// </summary>
    public ReadOnlyMemory<byte>? ChannelUpdate { get; init; }

    /// <summary>
    /// True when an erring hop could be identified.
    /// </summary>
    public bool IsAttributed => ErringHopIndex.HasValue;
}