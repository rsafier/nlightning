namespace NLightning.Domain.Gossip.Validation;

/// <summary>
/// What to do with a gossip message after a <see cref="GossipValidator"/> check.
/// </summary>
public enum GossipValidationOutcome
{
    /// <summary>Apply it to the graph (subject to the later signature and chain stages).</summary>
    Accept,

    /// <summary>Drop it silently.</summary>
    Ignore,

    /// <summary>Drop it and send the peer a connection <c>warning</c> (close the connection when
    /// <see cref="GossipValidationResult.CloseConnection"/>).</summary>
    Warn
}

/// <summary>
/// Why a gossip message was ignored or warned about.
/// </summary>
public enum GossipRejectReason
{
    /// <summary>Not rejected.</summary>
    None,

    /// <summary>The <c>chain_hash</c> is not our chain.</summary>
    UnknownChain,

    /// <summary><c>node_id_1</c> is not lexicographically less than <c>node_id_2</c>.</summary>
    NodeIdsNotOrdered,

    /// <summary>A node id or bitcoin key is not a compressed public key (33 bytes, prefix 02/03).</summary>
    InvalidPublicKey,

    /// <summary>The funding output has fewer than 6 confirmations.</summary>
    InsufficientDepth,

    /// <summary>A node of the message is blacklisted.</summary>
    BlacklistedNode,

    /// <summary>The same channel announcement is already known.</summary>
    AlreadyKnown,

    /// <summary>A different announcement for the same funding transaction is known (keys leaked).</summary>
    ConflictingAnnouncement,

    /// <summary>A <c>node_announcement</c> for a node without a known channel.</summary>
    UnknownNode,

    /// <summary>A <c>node_announcement</c> not newer than the stored one.</summary>
    NotNewer,

    /// <summary><c>addrlen</c> is too short for the address descriptors of known types.</summary>
    MalformedAddresses,

    /// <summary>A <c>channel_update</c> for a channel without an announcement that is not ours (an orphan).</summary>
    UnknownChannel,

    /// <summary>A <c>channel_update</c> without the <c>disable</c> bit for a spent channel.</summary>
    ChannelSpent,

    /// <summary>The same <c>channel_update</c> (same timestamp and fields) again.</summary>
    DuplicateUpdate,

    /// <summary>A <c>channel_update</c> with the stored timestamp but different fields.</summary>
    ConflictingSameTimestamp,

    /// <summary>A <c>channel_update</c> older than the stored one.</summary>
    OutdatedUpdate,

    /// <summary>A <c>channel_update</c> timestamp unreasonably far in the future.</summary>
    TimestampTooFarInFuture,

    /// <summary>A <c>channel_update</c> older than the stale limit (two weeks).</summary>
    StaleUpdate
}

/// <summary>
/// The result of a <see cref="GossipValidator"/> check.
/// </summary>
/// <param name="Outcome">What to do with the message.</param>
/// <param name="Reason">Why it was ignored or warned about (<see cref="GossipRejectReason.None"/> when accepted).
/// </param>
/// <param name="RequirementId">The plan requirement id (<c>B7-…</c>) the decision implements.</param>
/// <param name="CloseConnection">With <see cref="GossipValidationOutcome.Warn"/>: close the connection too.</param>
/// <param name="MayBlacklist">BOLT 7 allows blacklisting the origin node(s) for this.</param>
/// <param name="Forwardable">An accepted message may be queued for rebroadcast.</param>
/// <param name="Routable">An accepted channel or update may be used for routing.</param>
public sealed record GossipValidationResult(
    GossipValidationOutcome Outcome,
    GossipRejectReason Reason,
    string RequirementId,
    bool CloseConnection = false,
    bool MayBlacklist = false,
    bool Forwardable = true,
    bool Routable = true)
{
    /// <summary>True when the message should be applied.</summary>
    public bool IsAccepted => Outcome == GossipValidationOutcome.Accept;

    internal static GossipValidationResult Accept(string requirementId, bool forwardable = true, bool routable = true,
                                                  bool mayBlacklist = false) =>
        new(GossipValidationOutcome.Accept, GossipRejectReason.None, requirementId, false, mayBlacklist, forwardable,
            routable);

    internal static GossipValidationResult Ignore(GossipRejectReason reason, string requirementId,
                                                  bool mayBlacklist = false) =>
        new(GossipValidationOutcome.Ignore, reason, requirementId, false, mayBlacklist, false, false);

    internal static GossipValidationResult Warn(GossipRejectReason reason, string requirementId,
                                                bool closeConnection = false) =>
        new(GossipValidationOutcome.Warn, reason, requirementId, closeConnection, false, false, false);
}