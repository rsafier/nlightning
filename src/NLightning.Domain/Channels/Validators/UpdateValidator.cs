namespace NLightning.Domain.Channels.Validators;

using Commitments;
using Enums;
using Exceptions;

/// <summary>
/// BOLT 2 <c>update_add_htlc</c> and <c>update_fee</c> rules, evaluated against the prospective commitments of
/// <see cref="ChannelCommitments"/> (plan §1.2, §1.6; matrix §6.2, §6.3, §6.8).
/// </summary>
/// <remarks>
/// Local operations throw <see cref="CommitmentRefusedException"/>; peer input throws
/// <see cref="CommitmentViolationException"/>. Each exception carries the <c>B2-*</c> requirement id it enforces.
/// </remarks>
internal static class UpdateValidator
{
    /// <summary>
    /// Sender rules for an HTLC we offer: B2-ADD-S01..S09, B2-CLTV-02 and, when a limit is configured,
    /// B2-DUST-03/04.
    /// </summary>
    public static void ValidateSendAdd(ChannelCommitments commitments, HtlcRecord htlc, uint? currentBlockHeight)
    {
        var p = commitments.Params;
        if (htlc.AmountMsat == 0)
            throw Refused("B2-ADD-S05", "amount_msat must be greater than 0");
        if (htlc.AmountMsat < p.Remote.HtlcMinimumMsat)
            throw Refused("B2-ADD-S06",
                          $"amount_msat {htlc.AmountMsat} is below the peer's htlc_minimum_msat {p.Remote.HtlcMinimumMsat}");
        if (htlc.CltvExpiry >= ChannelCommitments.MaxCltvExpiry)
            throw Refused("B2-ADD-S07", $"cltv_expiry {htlc.CltvExpiry} must be below 500000000");
        if (currentBlockHeight is { } height && htlc.CltvExpiry <= height)
            throw Refused("B2-CLTV-02", $"cltv_expiry {htlc.CltvExpiry} is not after the current height {height}");

        var remoteView = commitments.BuildProspectiveView(CommitmentSide.Remote, htlc);
        var localView = commitments.BuildProspectiveView(CommitmentSide.Local, htlc);

        var offered = remoteView.Htlcs.Where(h => h.Direction == HtlcDirection.Outgoing).ToList();
        if (offered.Count > p.Remote.MaxAcceptedHtlcs)
            throw Refused("B2-ADD-S08", $"The peer accepts at most {p.Remote.MaxAcceptedHtlcs} HTLCs");
        var inFlight = offered.Aggregate(0UL, (sum, h) => checked(sum + h.AmountMsat));
        if (inFlight > p.Remote.MaxHtlcValueInFlightMsat)
            throw Refused("B2-ADD-S09",
                          $"{inFlight} msat in flight exceeds the peer's max_htlc_value_in_flight_msat {p.Remote.MaxHtlcValueInFlightMsat}");

        var reserve = (long)p.LocalReserveMsat;
        if (p.LocalIsFunder)
        {
            foreach (var view in (CommitmentView[])[remoteView, localView])
            {
                var holderDust = p.Holder(view.Holder).DustLimitSatoshis;
                var spec = view.ToSpec();
                var baseFee = (long)CommitmentFees.BaseCommitmentFee(spec, holderDust, p.OptionAnchors) * 1_000;
                if (view.LocalMsat - baseFee < reserve)
                    throw Refused("B2-ADD-S01",
                                  $"We could not pay the {view.Holder} commitment fee above our reserve after this HTLC");
                if (view.LocalMsat - (long)CommitmentFees.FunderCostMsat(spec, holderDust, p.OptionAnchors) < reserve)
                    throw Refused("B2-ADD-S02", "We could not pay both anchors above our reserve after this HTLC");
            }

            // Fee spike buffer: twice the feerate and one more non-dust HTLC on the peer's commitment.
            var spiked = remoteView.ToSpec(checked(remoteView.FeeratePerKw * 2));
            var spikeCost = (long)CommitmentFees.FunderCostMsat(spiked, p.Remote.DustLimitSatoshis, p.OptionAnchors)
                          + (long)(spiked.FeeratePerKw * CommitmentFees.HtlcOutputWeight / 1000) * 1_000;
            if (remoteView.LocalMsat - spikeCost < reserve)
                throw Refused("B2-ADD-S03", "The HTLC would leave no fee spike buffer (2x feerate, one more HTLC)");
        }
        else
        {
            if (remoteView.LocalMsat < reserve || localView.LocalMsat < reserve)
                throw Refused("B2-ADD-R02", "The HTLC would take our balance below our channel reserve");

            var funderCost = (long)CommitmentFees.FunderCostMsat(remoteView.ToSpec(), p.Remote.DustLimitSatoshis,
                                                                 p.OptionAnchors);
            if (remoteView.RemoteMsat - funderCost < (long)p.RemoteReserveMsat)
                throw Refused("B2-ADD-S04", "The funder could not pay the fee of its commitment after this HTLC");
        }

        if (p.MaxDustHtlcExposureMsat is { } maxDust)
        {
            CheckSendDustExposure(commitments, remoteView, htlc, maxDust, "B2-DUST-03");
            CheckSendDustExposure(commitments, localView, htlc, maxDust, "B2-DUST-04");
        }
    }

    /// <summary>
    /// Receiver rules for an HTLC the peer offers: B2-ADD-R01..R04 (R05: duplicate payment hashes are allowed; R07 is
    /// checked by the caller).
    /// </summary>
    public static void ValidateReceiveAdd(ChannelCommitments commitments, HtlcRecord htlc)
    {
        var p = commitments.Params;
        if (htlc.AmountMsat == 0 || htlc.AmountMsat < p.Local.HtlcMinimumMsat)
            throw Violation(commitments, "B2-ADD-R01",
                            $"amount_msat {htlc.AmountMsat} is 0 or below our htlc_minimum_msat {p.Local.HtlcMinimumMsat}");
        if (htlc.CltvExpiry >= ChannelCommitments.MaxCltvExpiry)
            throw Violation(commitments, "B2-ADD-R04", $"cltv_expiry {htlc.CltvExpiry} is not below 500000000");

        // BOLT 2 judges what the sender can afford, so our adds the peer has not signed yet are left out: it may have
        // offered this HTLC before it received them (crossed adds; found by the two-engine simulator).
        var localView = commitments.BuildProspectiveView(CommitmentSide.Local, htlc, peerView: true);
        var offered = localView.Htlcs.Where(h => h.Direction == HtlcDirection.Incoming).ToList();
        if (offered.Count > p.Local.MaxAcceptedHtlcs)
            throw Violation(commitments, "B2-ADD-R03", $"More than our max_accepted_htlcs {p.Local.MaxAcceptedHtlcs}");
        var inFlight = offered.Aggregate(0UL, (sum, h) => checked(sum + h.AmountMsat));
        if (inFlight > p.Local.MaxHtlcValueInFlightMsat)
            throw Violation(commitments, "B2-ADD-R03",
                            $"{inFlight} msat in flight exceeds our max_htlc_value_in_flight_msat {p.Local.MaxHtlcValueInFlightMsat}");

        var cost = p.LocalIsFunder
                       ? 0
                       : (long)CommitmentFees.FunderCostMsat(localView.ToSpec(), p.Local.DustLimitSatoshis,
                                                             p.OptionAnchors);
        if (localView.RemoteMsat - cost < (long)p.RemoteReserveMsat)
            throw Violation(commitments, "B2-ADD-R02",
                            "The peer cannot afford this HTLC (and the fee it pays) above its channel reserve");
    }

    /// <summary>
    /// A fee update we send must be payable above our reserve on the peer's next commitment (the peer checks
    /// B2-FEE-R03).
    /// </summary>
    public static void ValidateSendFee(ChannelCommitments next)
    {
        var p = next.Params;
        var view = next.BuildProspectiveView(CommitmentSide.Remote, feerateOverride: next.LatestFeeratePerKw);
        var cost = (long)CommitmentFees.FunderCostMsat(view.ToSpec(), p.Remote.DustLimitSatoshis, p.OptionAnchors);
        if (view.LocalMsat - cost < (long)p.LocalReserveMsat)
            throw Refused("B2-FEE-R03",
                          $"We could not pay feerate {next.LatestFeeratePerKw} above our reserve on the peer's commitment");
    }

    /// <summary>
    /// B2-FEE-R03: the funder (the peer) must be able to pay the new feerate on our <b>current</b> commitment
    /// (<see cref="ChannelCommitments.LocalCommit"/>), as BOLT 2 words it. BOLT 2 does not mention the reserve here, so
    /// only the fee (and anchors) must be covered.
    /// </summary>
    /// <remarks>Not the prospective view: that one also holds our own unsigned updates, which the funder may not have
    /// seen when it sent <c>update_fee</c>, so an honest funder would be rejected (two-engine simulator seed
    /// 152).</remarks>
    public static void ValidateReceiveFee(ChannelCommitments next)
    {
        var p = next.Params;
        var current = next.LocalCommit.Spec;
        var spec = new CommitmentSpec(current.Holder, next.LatestFeeratePerKw, current.LocalMsat, current.RemoteMsat,
                                      current.Htlcs);
        var cost = (long)CommitmentFees.FunderCostMsat(spec, p.Local.DustLimitSatoshis, p.OptionAnchors);
        if ((long)spec.RemoteMsat - cost < 0)
            throw Violation(next, "B2-FEE-R03",
                            $"The funder cannot afford feerate {next.LatestFeeratePerKw} on our commitment");
    }

    private static void CheckSendDustExposure(ChannelCommitments commitments, CommitmentView view, HtlcRecord htlc,
                                              ulong maxDust, string requirementId)
    {
        var p = commitments.Params;
        var holderDust = p.Holder(view.Holder).DustLimitSatoshis;
        if (!CommitmentFees.IsTrimmed(htlc.AmountMsat, htlc.IsOfferedBy(view.Holder), holderDust, view.FeeratePerKw,
                                      p.OptionAnchors))
            return;

        var exposure = CommitmentFees.TrimmedHtlcTotalMsat(view.ToSpec(), holderDust, p.OptionAnchors);
        if (exposure > maxDust)
            throw Refused(requirementId,
                          $"Dust exposure {exposure} msat on the {view.Holder} commitment would exceed {maxDust} msat");
    }

    private static CommitmentRefusedException Refused(string requirementId, string message) =>
        new(requirementId, message);

    private static CommitmentViolationException Violation(ChannelCommitments commitments, string requirementId,
                                                          string message) =>
        new(requirementId, message, commitments.ChannelId);
}