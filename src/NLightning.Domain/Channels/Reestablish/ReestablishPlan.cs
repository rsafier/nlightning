namespace NLightning.Domain.Channels.Reestablish;

/// <summary>What processing the peer's <c>channel_reestablish</c> leads to.</summary>
public enum ReestablishOutcome
{
    /// <summary>The channel resumes after the listed retransmissions.</summary>
    Resume,

    /// <summary>The peer's numbers or secret do not fit our state: send an <c>error</c> and fail the channel.</summary>
    Fail,

    /// <summary>
    /// The peer proved it has a newer state than ours (<c>option_data_loss_protect</c>): we must never broadcast or
    /// sign our commitment again, and ask the peer to fail the channel with an <c>error</c> (invariant I12).
    /// </summary>
    DataLoss
}

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
    UnsignedUpdates
}

/// <summary>
/// The result of <see cref="ReestablishPlanner.Plan"/>: the outcome and, when resuming, the messages to send in order.
/// </summary>
/// <param name="Outcome">Resume, fail, or data loss.</param>
/// <param name="Steps">The retransmissions in wire order (also for <see cref="ReestablishOutcome.Fail"/>: a
/// <see cref="ReestablishStep.TxAbort"/> may precede the error).</param>
/// <param name="RequirementId">The BOLT 2 requirement behind a failure (plan §6.9 ids).</param>
/// <param name="Reason">A description of a failure, for logs and the <c>error</c>.</param>
/// <param name="MustBroadcast">The spec says to broadcast our latest commitment (peer sent 0, B2-RE-14).</param>
public sealed record ReestablishPlan(
    ReestablishOutcome Outcome,
    IReadOnlyList<ReestablishStep> Steps,
    string? RequirementId = null,
    string? Reason = null,
    bool MustBroadcast = false)
{
    public static ReestablishPlan Resume(IReadOnlyList<ReestablishStep> steps) => new(ReestablishOutcome.Resume, steps);

    public static ReestablishPlan Failed(string requirementId, string reason, IReadOnlyList<ReestablishStep>? steps = null,
                                         bool mustBroadcast = false) =>
        new(ReestablishOutcome.Fail, steps ?? [], requirementId, reason, mustBroadcast);

    public static ReestablishPlan LostData(string reason) =>
        new(ReestablishOutcome.DataLoss, [], "B2-RE-23", reason);
}