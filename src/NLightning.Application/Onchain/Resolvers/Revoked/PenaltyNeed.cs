namespace NLightning.Application.Onchain.Resolvers.Revoked;

using Domain.Onchain.Models;

/// <summary>
/// One output the planner wants spent by a penalty (or our <c>to_remote</c> of the revoked commitment, which may share
/// the penalty transaction), with the row that tracks it.
/// </summary>
/// <param name="Row">The output's row (a second-level output has its own row).</param>
/// <param name="Input">How it is spent.</param>
/// <param name="DeadlineHeight">The first height at which the cheater can take it (BOLT 5 "expiry" of the revoked
/// output): <c>confirmation height + to_self_delay</c> for its <c>to_local</c> and its second-level outputs, the HTLC's
/// <c>cltv_expiry</c> for HTLC outputs; null for our <c>to_remote</c> (no competitor).</param>
/// <param name="Isolate">True when the cheater can already spend the output (its danger window is open) and the channel
/// has option_anchors: its <c>SIGHASH_SINGLE|SIGHASH_ANYONECANPAY</c> HTLC transactions can then be pinned in the
/// mempool, so the output is penalized in its own transaction from the start instead of sharing a batch that such a
/// pin would hold back (BOLT 5 §Revoked Transaction Close Handling rationale, plan O7-T3).</param>
public sealed record PenaltyNeed(OutputResolutionModel Row, SweepInput Input, uint? DeadlineHeight,
                                 bool Isolate = false)
{
    /// <summary>True when the input is spent with the revocation key.</summary>
    public bool IsPenalty => Input.SpendKind is Domain.Onchain.Enums.SweepSpendKind.RevokedDelayedOutput
                                              or Domain.Onchain.Enums.SweepSpendKind.RevokedHtlc;
}