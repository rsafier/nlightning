namespace NLightning.Domain.Channels.Reestablish;

using Bitcoin.ValueObjects;

/// <summary>A message to (re)send after the peer's <c>channel_reestablish</c>, in wire order.</summary>
public enum ReestablishStep
{
    /// <summary><c>tx_abort</c>: the peer named an interactive funding tx a v1 channel does not have (B2-RE-25).</summary>
    TxAbort,

    /// <summary><c>channel_ready</c>: both next commitment numbers are 1 (B2-RE-15).</summary>
    ChannelReady,

    /// <summary>Our last <c>revoke_and_ack</c>, regenerated from the seed (B2-RE-20, D4).</summary>
    RevokeAndAck,

    /// <summary>The stored updates and <c>commitment_signed</c>, verbatim (B2-RE-18, D4).</summary>
    CommitDiff,

    /// <summary>Our persisted updates no <c>commitment_signed</c> covers yet, with their original ids.</summary>
    UnsignedUpdates,

    /// <summary>
    /// Our <c>commitment_signed</c> for the latest interactive funding transaction again: the peer's
    /// <c>next_funding</c> names it with the <c>commitment_signed</c> bit and we have not received its
    /// <c>tx_signatures</c> (SP-RE-03). Byte-identical to the original (splicing plan SP2-A-T2).
    /// </summary>
    NextFundingCommitmentSigned,

    /// <summary>
    /// Our <c>tx_signatures</c> for the latest interactive funding transaction: the peer's <c>next_funding</c> names
    /// it and either we received its <c>tx_signatures</c>, or we received its <c>commitment_signed</c> and sign first
    /// (SP-RE-03).
    /// </summary>
    NextFundingTxSignatures,

    /// <summary>
    /// Our <c>announcement_signatures</c> for the funding the peer's <c>my_current_funding_locked</c> names, with its
    /// bit 0 set, when the channel is public and we are ready to send them (SP-RE-04, SP-G-01).
    /// </summary>
    AnnouncementSignatures
}

/// <summary>
/// The result of <see cref="ReestablishPlanner.Plan"/>: the channel resumes after the listed retransmissions, the
/// channel fails, or the peer proved that we lost data.
/// </summary>
public union ReestablishPlan(ReestablishPlan.Resume, ReestablishPlan.Fail, ReestablishPlan.DataLoss)
{
    /// <summary>The channel resumes after the listed retransmissions.</summary>
    /// <param name="Steps">The retransmissions in wire order.</param>
    /// <param name="PeerSpliceLocked">The pending splice the peer's <c>my_current_funding_locked</c> names and whose
    /// <c>splice_locked</c> we have not received: process it as a received <c>splice_locked</c> (SP-RE-04), before the
    /// steps. Null otherwise (splicing plan SP2-0; set by lane SP2-A, processed through lane SP2-B's
    /// <c>ISpliceService.HandlePeerFundingLockedAsync</c>).</param>
    public sealed record Resume(IReadOnlyList<ReestablishStep> Steps, TxId? PeerSpliceLocked = null);

    /// <summary>The peer's numbers or secret do not fit our state: send an <c>error</c> and fail the channel.</summary>
    /// <param name="RequirementId">The BOLT 2 requirement behind the failure (plan §6.9 ids).</param>
    /// <param name="Reason">A description of the failure, for logs and the <c>error</c>.</param>
    /// <param name="Steps">The retransmissions before the error, in wire order (a <see cref="ReestablishStep.TxAbort"/>
    /// may precede it).</param>
    /// <param name="MustBroadcast">The spec says to broadcast our latest commitment (peer sent 0, B2-RE-14).</param>
    public sealed record Fail(string RequirementId, string Reason, IReadOnlyList<ReestablishStep> Steps,
                              bool MustBroadcast = false);

    /// <summary>
    /// The peer proved it has a newer state than ours (<c>option_data_loss_protect</c>): we must never broadcast or
    /// sign our commitment again, and ask the peer to fail the channel with an <c>error</c> (invariant I12, B2-RE-23).
    /// </summary>
    /// <param name="Reason">A description of the loss, for logs and the <c>error</c>.</param>
    public sealed record DataLoss(string Reason)
    {
        /// <summary>The BOLT 2 requirement behind it.</summary>
        public const string RequirementId = "B2-RE-23";
    }
}