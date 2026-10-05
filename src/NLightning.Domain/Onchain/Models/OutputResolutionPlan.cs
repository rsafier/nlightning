namespace NLightning.Domain.Onchain.Models;

using Enums;

/// <summary>
/// One thing to do for an output (BOLT 5 plan §3.1), what <see cref="Planners.OutputResolutionPlanner"/> asks the
/// watcher to do. Each case carries the BOLT 5 plan requirement (<c>B5-…</c>) behind it.
/// </summary>
public union ResolutionAction(
    ResolutionAction.Wait,
    ResolutionAction.BroadcastHtlcTimeoutTx,
    ResolutionAction.BroadcastHtlcSuccessTx,
    ResolutionAction.Sweep,
    ResolutionAction.RaiseFulfilled,
    ResolutionAction.RaiseFailed,
    ResolutionAction.AlertLostFunds)
{
    /// <summary>Nothing to do before the tip reaches <paramref name="UntilHeight"/>.</summary>
    /// <param name="UntilHeight">The tip height from which the next step can be taken (a transaction broadcast at
    /// that tip can enter the next block).</param>
    public sealed record Wait(string RequirementId, uint UntilHeight);

    /// <summary>Broadcast our pre-signed HTLC-timeout transaction for the output (B5-LCL-LO-02).</summary>
    public sealed record BroadcastHtlcTimeoutTx(string RequirementId);

    /// <summary>Broadcast our HTLC-success transaction with <paramref name="Preimage"/> (B5-LCL-RO-01).</summary>
    /// <param name="DeadlineHeight">The HTLC's <c>cltv_expiry</c>, from which the peer can time it out.</param>
    public sealed record BroadcastHtlcSuccessTx(string RequirementId, uint DeadlineHeight, byte[] Preimage);

    /// <summary>Broadcast a sweep, claim or penalty spending the output (or its second-level output).</summary>
    /// <param name="SpendKind">How the output is spent.</param>
    /// <param name="OnSecondLevel">True when the spent output is vout 0 of the HTLC transaction that spent the
    /// commitment output (<see cref="OutputResolutionFacts.Spend"/>), false for the commitment output itself.</param>
    /// <param name="DeadlineHeight">The height from which a competitor can take the output (the fee policy's
    /// deadline, §3.7); null when there is none.</param>
    /// <param name="Preimage">The preimage of a <see cref="SweepSpendKind.HtlcPreimageClaim"/>.</param>
    public sealed record Sweep(string RequirementId, SweepSpendKind SpendKind, bool OnSecondLevel = false,
                               uint? DeadlineHeight = null, byte[]? Preimage = null);

    /// <summary>Raise <c>OutgoingHtlcFulfilled</c> for our offered HTLC with the preimage (persist it first, BOLT2
    /// I10).</summary>
    public sealed record RaiseFulfilled(string RequirementId, byte[] Preimage);

    /// <summary>Raise <c>OutgoingHtlcFailed</c> (on-chain timeout, no reason bytes; O3-T4) for our offered
    /// HTLC.</summary>
    public sealed record RaiseFailed(string RequirementId);

    /// <summary>Warn the user: funds of this output went to the peer by a path it should not have had
    /// (B5-GEN-06).</summary>
    public sealed record AlertLostFunds(string RequirementId);
}

/// <summary>
/// The planner's answer for one output: its state and the actions to take now, in order.
/// </summary>
public sealed record OutputResolutionPlan(PlannedResolutionState State, IReadOnlyList<ResolutionAction> Actions);