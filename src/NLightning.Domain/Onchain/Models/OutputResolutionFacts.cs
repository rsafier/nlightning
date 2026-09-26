namespace NLightning.Domain.Onchain.Models;

using Bitcoin.ValueObjects;
using Enums;

/// <summary>
/// A confirmed spend of an output (BOLT 5 plan §3.2 "Block"): who spent it and how.
/// </summary>
/// <param name="SpendingTxId">The spending transaction.</param>
/// <param name="Height">The height of the block that confirmed it.</param>
/// <param name="ByUs">True when it is a transaction we broadcast (its txid is one of our resolving transactions).</param>
/// <param name="Path">The script path of the spending witness (<c>HtlcWitnessParser.Parse</c>) for an HTLC output;
/// <see cref="HtlcSpendPath.Unknown"/> otherwise.</param>
/// <param name="Preimage">The preimage the witness revealed, already checked against the payment hash
/// (<c>HtlcWitnessParser.TryExtractPreimage</c>); null when none.</param>
public sealed record OutputSpend(
    TxId SpendingTxId,
    uint Height,
    bool ByUs,
    HtlcSpendPath Path = HtlcSpendPath.Unknown,
    byte[]? Preimage = null);

/// <summary>
/// The chain facts <see cref="Planners.OutputResolutionPlanner.Plan"/> decides from. The planner keeps no state: the
/// watcher runs it again on every block with fresh facts, so an action is repeated until its effect is seen on chain
/// (broadcasts are deduplicated by the persisted broadcast rows, switch events by the idempotent switch).
/// </summary>
/// <param name="TipHeight">The height of the current tip.</param>
/// <param name="CommitmentHeight">The height of the block that confirmed the commitment.</param>
/// <param name="Spend">The confirmed spend of the commitment output, if any.</param>
/// <param name="SecondLevelSpend">When <paramref name="Spend"/> is an HTLC-timeout/success transaction (ours or the
/// cheater's): the confirmed spend of its output, if any.</param>
/// <param name="AllowedPreimage">The preimage we may use for this HTLC: <c>HtlcRecord.KnownPreimage</c> or our fulfill
/// of it, never an invoice preimage the switch did not accept for this HTLC (B5-LCL-RO-02).</param>
/// <param name="RemoteIrrevocablyCommitted">For an HTLC the peer offered: whether the peer is irrevocably committed to
/// it (engine state at least <c>RcvdAddAckRevocation</c>, 34). Never claim it otherwise (B5-LCL-RO-03,
/// B5-RMT-RO-02).</param>
/// <param name="UpstreamResolved">For an HTLC we offered: the upstream fulfill or fail was already raised and
/// persisted, so no switch event is asked for again.</param>
/// <param name="SecondLevelCsvDelay">The CSV delay of an HTLC transaction's output: the holder's counterparty's
/// <c>to_self_delay</c> (ours: <c>ChannelParams.Remote.ToSelfDelay</c>; a cheater's: <c>Local.ToSelfDelay</c>). Needed
/// only once a second-level transaction confirmed.</param>
/// <param name="ReasonableDepth">The depth at which an upstream HTLC is failed (<c>Onchain:ReasonableDepth</c>, D9).</param>
/// <param name="IrrevocableDepth">The depth at which a resolution is irrevocable (100, B5-GEN-02).</param>
public sealed record OutputResolutionFacts(
    uint TipHeight,
    uint CommitmentHeight,
    OutputSpend? Spend = null,
    OutputSpend? SecondLevelSpend = null,
    byte[]? AllowedPreimage = null,
    bool RemoteIrrevocablyCommitted = true,
    bool UpstreamResolved = false,
    ushort SecondLevelCsvDelay = 0,
    uint ReasonableDepth = OutputResolutionFacts.DefaultReasonableDepth,
    uint IrrevocableDepth = OutputResolutionFacts.DefaultIrrevocableDepth)
{
    /// <summary>D9: <c>Onchain:ReasonableDepth</c> default.</summary>
    public const uint DefaultReasonableDepth = 6;

    /// <summary>B5-GEN-02: 100 blocks.</summary>
    public const uint DefaultIrrevocableDepth = 100;

    /// <summary>How many blocks deep a transaction confirmed at <paramref name="height"/> is (1 in the tip block).</summary>
    public uint DepthOf(uint height) => TipHeight >= height ? TipHeight - height + 1 : 0;
}

/// <summary>
/// The facts <see cref="Planners.OutputResolutionPlanner.PlanHtlcWithoutOutput"/> decides from: a committed HTLC with
/// no output in the commitment on chain (trimmed, not yet in it, or already removed from it; B5-LCL-LO-04,
/// B5-RMT-LO-03, B5-REV-RES-03).
/// </summary>
/// <param name="TipHeight">The height of the current tip.</param>
/// <param name="CommitmentHeight">The height of the block that confirmed the commitment.</param>
/// <param name="AllowedPreimage">The preimage we know for the HTLC, if any.</param>
/// <param name="OutputInAnyValidCommitment">Whether another valid commitment (our current, the peer's current or next)
/// has an output for it. When none does, the HTLC may be failed at once.</param>
/// <param name="UpstreamResolved">The upstream event was already raised and persisted.</param>
/// <param name="ReasonableDepth">See <see cref="OutputResolutionFacts.ReasonableDepth"/>.</param>
/// <param name="IrrevocableDepth">See <see cref="OutputResolutionFacts.IrrevocableDepth"/>.</param>
public sealed record HtlcWithoutOutputFacts(
    uint TipHeight,
    uint CommitmentHeight,
    byte[]? AllowedPreimage = null,
    bool OutputInAnyValidCommitment = true,
    bool UpstreamResolved = false,
    uint ReasonableDepth = OutputResolutionFacts.DefaultReasonableDepth,
    uint IrrevocableDepth = OutputResolutionFacts.DefaultIrrevocableDepth);