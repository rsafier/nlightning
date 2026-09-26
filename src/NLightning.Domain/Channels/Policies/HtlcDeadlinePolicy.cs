namespace NLightning.Domain.Channels.Policies;

using Commitments;
using Enums;

/// <summary>
/// What a node must do about one HTLC at a given block height (BOLT 2 "Risks With HTLC Timeouts", BOLT2 plan N9-T2).
/// </summary>
public enum HtlcDeadlineAction : byte
{
    /// <summary>Nothing to do yet (or nothing this node can do).</summary>
    None = 0,

    /// <summary>
    /// Fail the incoming HTLC back upstream (<c>update_fail_htlc</c>) now: we cannot resolve it in time any more and
    /// failing it is safe (we know no preimage and no outgoing HTLC continues it).
    /// </summary>
    FailBackUpstream = 1,

    /// <summary>
    /// Fail the channel (broadcast our latest commitment): the HTLC must be resolved on chain before its expiry.
    /// </summary>
    FailChannel = 2
}

/// <summary>
/// What an incoming HTLC that is locked in and not removed yet is waiting for, as far as its deadline goes.
/// </summary>
public enum IncomingHtlcResolution : byte
{
    /// <summary>
    /// We know no preimage for it and no outgoing HTLC continues it (never forwarded, or the forward failed): failing
    /// it back is safe.
    /// </summary>
    Unresolved = 0,

    /// <summary>
    /// We know its preimage (our settled invoice, or the downstream HTLC was fulfilled) but the fulfill is not
    /// committed with the peer (for example the peer is away): it must be claimed on chain if it stays too long.
    /// </summary>
    PreimageKnown = 1,

    /// <summary>
    /// An outgoing HTLC continues it and is still unresolved: failing it upstream would lose the amount if the
    /// downstream peer fulfills. The outgoing HTLC's own timeout deadline protects it.
    /// </summary>
    AwaitingDownstream = 2,

    /// <summary>
    /// We know no preimage for it yet and it was never forwarded (no circuit, no outgoing HTLC): a final-hop candidate
    /// the switch may still settle (for example a fulfill deferred while the link is down). Failing it back is safe,
    /// but the forwarding distance does not apply (the payer's final <c>cltv_expiry</c> is only about
    /// <c>min_final_cltv_expiry</c> away): it is failed back at the fulfillment deadline instead (B2-CLTV-05).
    /// </summary>
    UnresolvedFinalHop = 3
}

/// <summary>
/// One deadline decision: the action and the BOLT 2 plan requirement that asks for it.
/// </summary>
/// <param name="Action">What to do.</param>
/// <param name="RequirementId">The <c>B2-*</c> row of the BOLT 2 plan's traceability matrix, when there is one.</param>
/// <param name="DeadlineHeight">The height the action is due at (the height at which it first applies).</param>
public readonly record struct HtlcDeadlineDecision(HtlcDeadlineAction Action, string? RequirementId,
                                                   uint DeadlineHeight)
{
    public static HtlcDeadlineDecision None => default;
}

/// <summary>
/// The BOLT 2 HTLC deadlines (BOLT2 plan N9-T2, B2-CLTV-01/03/04/05/06, B2-FWD-03): pure rules over the HTLC
/// record, its commitment membership and the block height.
/// </summary>
/// <remarks>
/// <para>BOLT 2 "Risks With HTLC Timeouts":</para>
/// <list type="bullet">
///   <item><b>Offered HTLCs</b>: the timeout deadline is <see cref="GraceBlocks"/> (G, "1 or 2 blocks is reasonable")
///   after <c>cltv_expiry</c>. "if an HTLC which it offered is in either node's current commitment transaction, AND is
///   past this timeout deadline: MUST fail the channel." (B2-CLTV-01, B2-CLTV-03)</item>
///   <item><b>Received HTLCs we fulfilled</b>: the fulfillment deadline is <see cref="FulfillSafetyBlocks"/>
///   ("2R+G+S ... 18 blocks is reasonable") before <c>cltv_expiry</c>. "if an HTLC it has fulfilled is in either
///   node's current commitment transaction, AND is past this fulfillment deadline: MUST fail the channel."
///   (B2-CLTV-04, B2-CLTV-06). We apply the same deadline to an incoming HTLC whose preimage we know but whose fulfill
///   is not committed yet (<see cref="IncomingHtlcResolution.PreimageKnown"/>): it too has to be claimed on chain.</item>
///   <item><b>Received HTLCs we cannot resolve</b> (no preimage, nothing downstream): they are failed back upstream
///   <see cref="FailBackBlocks"/> before <c>cltv_expiry</c> (B2-FWD-03; our value, by default the node's
///   <c>cltv_expiry_delta</c>: an HTLC this close to its expiry could not be forwarded any more, and the upstream peer
///   would otherwise have to go on chain at <c>cltv_expiry + G</c>). This is also BOLT 2's "MUST fail (and not
///   forward) an HTLC whose fulfillment deadline is already past" (B2-CLTV-05), because
///   <see cref="FailBackBlocks"/> &gt;= <see cref="FulfillSafetyBlocks"/>.</item>
///   <item><b>Received HTLCs never forwarded and not settled</b> (<see cref="IncomingHtlcResolution.UnresolvedFinalHop"/>):
///   failed back at the fulfillment deadline (B2-CLTV-05). The forwarding distance would fail a final-hop HTLC
///   almost at once, since a payer's <c>cltv_expiry</c> is only about <c>min_final_cltv_expiry</c> ahead.</item>
/// </list>
/// <para>"Past a deadline" means <c>height &gt;= deadline</c>: the action is due at the deadline height itself.</para>
/// </remarks>
public sealed record HtlcDeadlinePolicy
{
    /// <summary>BOLT 2's G for offered HTLCs (the plan's default).</summary>
    public const uint DefaultGraceBlocks = 2;

    /// <summary>BOLT 2's fulfillment deadline (2R+G+S, "18 blocks is reasonable").</summary>
    public const uint DefaultFulfillSafetyBlocks = 18;

    /// <summary>The default fail-back distance: BOLT 2's minimum <c>cltv_expiry_delta</c> (34).</summary>
    public const uint DefaultFailBackBlocks = 34;

    /// <summary>Blocks after <c>cltv_expiry</c> at which an offered HTLC still in a commitment fails the channel.</summary>
    public uint GraceBlocks { get; }

    /// <summary>Blocks before <c>cltv_expiry</c> at which a fulfilled (or preimage-known) received HTLC still in a
    /// commitment fails the channel.</summary>
    public uint FulfillSafetyBlocks { get; }

    /// <summary>Blocks before <c>cltv_expiry</c> at which an unresolved received HTLC is failed back upstream.</summary>
    public uint FailBackBlocks { get; }

    /// <exception cref="ArgumentOutOfRangeException"><paramref name="failBackBlocks"/> is below
    /// <paramref name="fulfillSafetyBlocks"/> (an HTLC must be failed back before its fulfillment deadline, B2-CLTV-05).
    /// </exception>
    public HtlcDeadlinePolicy(uint graceBlocks = DefaultGraceBlocks,
                              uint fulfillSafetyBlocks = DefaultFulfillSafetyBlocks,
                              uint failBackBlocks = DefaultFailBackBlocks)
    {
        if (failBackBlocks < fulfillSafetyBlocks)
            throw new ArgumentOutOfRangeException(nameof(failBackBlocks), failBackBlocks,
                                                  $"Must be at least the fulfillment deadline ({fulfillSafetyBlocks})");

        GraceBlocks = graceBlocks;
        FulfillSafetyBlocks = fulfillSafetyBlocks;
        FailBackBlocks = failBackBlocks;
    }

    /// <summary>The height at which an offered HTLC with <paramref name="cltvExpiry"/> fails the channel.</summary>
    public uint OfferedDeadline(uint cltvExpiry) => (uint)Math.Min(uint.MaxValue, (ulong)cltvExpiry + GraceBlocks);

    /// <summary>The height at which a fulfilled received HTLC with <paramref name="cltvExpiry"/> fails the channel.
    /// </summary>
    public uint FulfillDeadline(uint cltvExpiry) => cltvExpiry > FulfillSafetyBlocks ? cltvExpiry - FulfillSafetyBlocks : 0;

    /// <summary>The height at which an unresolved received HTLC with <paramref name="cltvExpiry"/> is failed back.
    /// </summary>
    public uint FailBackHeight(uint cltvExpiry) => cltvExpiry > FailBackBlocks ? cltvExpiry - FailBackBlocks : 0;

    /// <summary>
    /// The decision for <paramref name="htlc"/> at <paramref name="height"/>.
    /// </summary>
    /// <param name="htlc">The HTLC as the commitment engine holds it.</param>
    /// <param name="height">The current block height.</param>
    /// <param name="incomingResolution">For an incoming HTLC that is locked in and not removed: what it waits for.
    /// Ignored otherwise. The caller works it out (invoices, forward circuits, the outgoing HTLC).</param>
    public HtlcDeadlineDecision Evaluate(HtlcRecord htlc, uint height,
                                         IncomingHtlcResolution incomingResolution = IncomingHtlcResolution.Unresolved)
    {
        ArgumentNullException.ThrowIfNull(htlc);
        if (!HtlcStateTable.IsDefined(htlc.State))
            return HtlcDeadlineDecision.None;

        var inEitherCommit = HtlcStateTable.IsInLocalCommit(htlc.State) || HtlcStateTable.IsInRemoteCommit(htlc.State);

        if (htlc.Direction == HtlcDirection.Outgoing)
        {
            // B2-CLTV-03: an HTLC we offered, in either current commitment, past cltv_expiry + G
            var deadline = OfferedDeadline(htlc.CltvExpiry);
            return inEitherCommit && height >= deadline
                       ? new HtlcDeadlineDecision(HtlcDeadlineAction.FailChannel, "B2-CLTV-03", deadline)
                       : HtlcDeadlineDecision.None;
        }

        // Incoming: we fulfilled it (a fulfill sent, not yet gone from both commitments)
        if (htlc.Removal is { IsFulfill: true })
        {
            var deadline = FulfillDeadline(htlc.CltvExpiry);
            return inEitherCommit && height >= deadline
                       ? new HtlcDeadlineDecision(HtlcDeadlineAction.FailChannel, "B2-CLTV-06", deadline)
                       : HtlcDeadlineDecision.None;
        }

        // Failed by us, or not locked in yet: the peer's deadline, not ours
        if (htlc.Removal is not null || htlc.State != HtlcState.RcvdAddAckRevocation)
            return HtlcDeadlineDecision.None;

        switch (incomingResolution)
        {
            case IncomingHtlcResolution.PreimageKnown:
                {
                    var deadline = FulfillDeadline(htlc.CltvExpiry);
                    return height >= deadline
                               ? new HtlcDeadlineDecision(HtlcDeadlineAction.FailChannel, "B2-CLTV-06", deadline)
                               : HtlcDeadlineDecision.None;
                }
            case IncomingHtlcResolution.UnresolvedFinalHop:
                {
                    // B2-CLTV-05: never keep an HTLC whose fulfillment deadline is past
                    var deadline = FulfillDeadline(htlc.CltvExpiry);
                    return height >= deadline
                               ? new HtlcDeadlineDecision(HtlcDeadlineAction.FailBackUpstream, "B2-CLTV-05", deadline)
                               : HtlcDeadlineDecision.None;
                }
            case IncomingHtlcResolution.Unresolved:
                {
                    var deadline = FailBackHeight(htlc.CltvExpiry);
                    return height >= deadline
                               ? new HtlcDeadlineDecision(HtlcDeadlineAction.FailBackUpstream, "B2-FWD-03", deadline)
                               : HtlcDeadlineDecision.None;
                }
            default:
                return HtlcDeadlineDecision.None;
        }
    }

    /// <summary>
    /// True when <paramref name="htlc"/> is an incoming HTLC that is locked in and not removed, and old enough that
    /// its <see cref="IncomingHtlcResolution"/> matters at <paramref name="height"/> (so a caller looks it up only then).
    /// </summary>
    public bool NeedsIncomingResolution(HtlcRecord htlc, uint height)
    {
        ArgumentNullException.ThrowIfNull(htlc);
        return htlc is { Direction: HtlcDirection.Incoming, Removal: null, State: HtlcState.RcvdAddAckRevocation }
            && height >= Math.Min(FailBackHeight(htlc.CltvExpiry), FulfillDeadline(htlc.CltvExpiry));
    }
}