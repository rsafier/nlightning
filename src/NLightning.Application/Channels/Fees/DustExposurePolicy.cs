namespace NLightning.Application.Channels.Fees;

using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;

/// <summary>
/// BOLT 2 "Bounding exposure to trimmed in-flight HTLCs: <c>max_dust_htlc_exposure_msat</c>" (BOLT2 plan N9-T3,
/// B2-DUST-01..05): what trimmed HTLCs would hold on either commitment. Pure; the sender rules (B2-DUST-03/04) are
/// enforced by the commitment engine (<c>UpdateValidator</c>) from <see cref="CommitmentParams.MaxDustHtlcExposureMsat"/>.
/// </summary>
public static class DustExposurePolicy
{
    /// <summary>
    /// The limit that applies to a channel: the one stored with its commitment state (NL-242), else the node's
    /// <c>NodeOptions.MaxDustHtlcExposureMsat</c> (a state created before the option existed has none). Null: no
    /// limit.
    /// </summary>
    public static ulong? Resolve(ChannelCommitments commitments, ulong? nodeLimitMsat)
    {
        ArgumentNullException.ThrowIfNull(commitments);
        return commitments.Params.MaxDustHtlcExposureMsat ?? nodeLimitMsat;
    }

    /// <summary>
    /// The dust exposure (msat) of the next <paramref name="side"/> commitment at <paramref name="feeratePerKw"/>:
    /// the sum of the HTLCs trimmed from it, counting every HTLC not yet removed from it (pending adds included).
    /// </summary>
    public static ulong ProspectiveExposureMsat(ChannelCommitments commitments, CommitmentSide side,
                                                uint feeratePerKw)
    {
        ArgumentNullException.ThrowIfNull(commitments);
        var p = commitments.Params;
        var view = commitments.BuildProspectiveView(side, feerateOverride: feeratePerKw);
        return CommitmentFeeCalculator.TrimmedHtlcTotalMsat(view.ToSpec(), p.Holder(side).DustLimitSatoshis,
                                                            p.OptionAnchors);
    }

    /// <summary>
    /// B2-DUST-05 / B2-FEE-S03: on a channel without <c>option_anchors</c>, a feerate increase trims more HTLCs.
    /// Returns the first commitment whose dust exposure at <paramref name="feeratePerKw"/> exceeds
    /// <paramref name="maxDustMsat"/> (BOLT 2: the sender MAY NOT send such an <c>update_fee</c>), or null.
    /// </summary>
    /// <remarks>Only increases are judged: a lower feerate never trims more. With anchors the HTLC transactions
    /// pay no fee, so the trim threshold does not depend on the feerate.</remarks>
    public static DustExposureExcess? CheckFeeIncrease(ChannelCommitments commitments, uint feeratePerKw,
                                                       ulong maxDustMsat)
    {
        ArgumentNullException.ThrowIfNull(commitments);
        if (commitments.Params.OptionAnchors || feeratePerKw <= commitments.LatestFeeratePerKw)
            return null;

        foreach (var side in (CommitmentSide[])[CommitmentSide.Remote, CommitmentSide.Local])
        {
            var exposure = ProspectiveExposureMsat(commitments, side, feeratePerKw);
            if (exposure > maxDustMsat)
                return new DustExposureExcess(side, exposure, maxDustMsat);
        }

        return null;
    }

    /// <summary>
    /// B2-DUST-01/02, the receiver rules, for an incoming HTLC that is now locked in (in both commitments): when it is
    /// trimmed from a commitment and it, plus the trimmed HTLCs that commitment already held when it arrived, exceeds
    /// <paramref name="maxDustMsat"/>, BOLT 2 says to fail it once committed and never reveal its preimage. Returns the
    /// first such commitment, or null (the HTLC may be accepted).
    /// </summary>
    /// <remarks>
    /// "When it arrived" is approximated deterministically from the committed state: every HTLC in the commitment
    /// counts except the incoming ones with this id or a higher one (they arrived after it). Outgoing HTLCs all count,
    /// so the result never lets through more than the rule; the same state gives the same answer on every replay.
    /// The commitment's own feerate decides the trimming (BOLT 3).
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="htlc"/> is not an incoming HTLC.</exception>
    public static DustExposureExcess? CheckLockedInIncoming(ChannelCommitments commitments, HtlcRecord htlc,
                                                            ulong maxDustMsat)
    {
        ArgumentNullException.ThrowIfNull(commitments);
        ArgumentNullException.ThrowIfNull(htlc);
        if (htlc.Direction != HtlcDirection.Incoming)
            throw new ArgumentException("Only an incoming HTLC is judged by the receiver rules", nameof(htlc));

        var p = commitments.Params;
        // BOLT 2 lists the remote transaction first; the order only picks which excess is reported
        foreach (var side in (CommitmentSide[])[CommitmentSide.Remote, CommitmentSide.Local])
        {
            var spec = commitments.BuildSpec(side);
            var holderDust = p.Holder(side).DustLimitSatoshis;
            if (!CommitmentFeeCalculator.IsHtlcTrimmed(htlc.AmountMsat, htlc.IsOfferedBy(side), holderDust,
                                                       spec.FeeratePerKw, p.OptionAnchors))
                continue;

            var earlier = spec.Htlcs.Where(h => h.Direction == HtlcDirection.Outgoing || h.Id < htlc.Id)
                              .Where(h => CommitmentFeeCalculator.IsHtlcTrimmed(spec, h, holderDust, p.OptionAnchors))
                              .Aggregate(0UL, (sum, h) => checked(sum + h.AmountMsat));
            var exposure = checked(earlier + htlc.AmountMsat);
            if (exposure > maxDustMsat)
                return new DustExposureExcess(side, exposure, maxDustMsat);
        }

        return null;
    }
}

/// <summary>
/// A commitment whose dust exposure would exceed the limit.
/// </summary>
/// <param name="Holder">The holder of the commitment.</param>
/// <param name="ExposureMsat">Its dust exposure (msat).</param>
/// <param name="MaxDustMsat">The limit.</param>
public sealed record DustExposureExcess(CommitmentSide Holder, ulong ExposureMsat, ulong MaxDustMsat)
{
    public override string ToString() =>
        $"dust exposure {ExposureMsat} msat on the {Holder} commitment exceeds max_dust_htlc_exposure_msat {MaxDustMsat}";
}