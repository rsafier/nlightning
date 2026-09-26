namespace NLightning.Domain.Onchain.Models;

using Enums;

/// <summary>
/// One thing to do for an output (BOLT 5 plan §3.1).
/// </summary>
/// <param name="Kind">What to do.</param>
/// <param name="RequirementId">The BOLT 5 plan requirement (<c>B5-…</c>) behind it.</param>
/// <param name="SpendKind">For <see cref="ResolutionActionKind.Sweep"/>: how the output is spent.</param>
/// <param name="OnSecondLevel">For <see cref="ResolutionActionKind.Sweep"/>: true when the spent output is vout 0 of
/// the HTLC transaction that spent the commitment output (<see cref="OutputResolutionFacts.Spend"/>), false for the
/// commitment output itself.</param>
/// <param name="WaitUntilHeight">For <see cref="ResolutionActionKind.Wait"/>: the tip height from which the next step
/// can be taken (a transaction broadcast at that tip can enter the next block).</param>
/// <param name="DeadlineHeight">The height from which a competitor can take the output (the fee policy's deadline,
/// §3.7); null when there is none.</param>
/// <param name="Preimage">For <see cref="ResolutionActionKind.BroadcastHtlcSuccessTx"/>,
/// <see cref="ResolutionActionKind.RaiseFulfilled"/> and a preimage claim.</param>
public sealed record ResolutionAction(
    ResolutionActionKind Kind,
    string RequirementId,
    SweepSpendKind? SpendKind = null,
    bool OnSecondLevel = false,
    uint? WaitUntilHeight = null,
    uint? DeadlineHeight = null,
    byte[]? Preimage = null);

/// <summary>
/// The planner's answer for one output: its state and the actions to take now, in order.
/// </summary>
public sealed record OutputResolutionPlan(PlannedResolutionState State, IReadOnlyList<ResolutionAction> Actions)
{
    /// <summary>True when an action of <paramref name="kind"/> is asked for.</summary>
    public bool Has(ResolutionActionKind kind) => Actions.Any(a => a.Kind == kind);

    /// <summary>The first action of <paramref name="kind"/>, or null.</summary>
    public ResolutionAction? Get(ResolutionActionKind kind) => Actions.FirstOrDefault(a => a.Kind == kind);
}