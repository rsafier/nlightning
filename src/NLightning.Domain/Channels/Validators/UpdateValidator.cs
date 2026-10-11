namespace NLightning.Domain.Channels.Validators;

using Bitcoin.Transactions.Constants;
using Bitcoin.Transactions.Enums;
using Bitcoin.Transactions.Factories;
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

        // Splicing SP-OP-01 / SP-I6: the HTLC must be valid on every active funding (the current one first; only the
        // main balances and the reserve (D9) differ between them)
        foreach (var funding in commitments.ActiveFundingViews())
            ValidateSendAddBalances(p, remoteView.Shift(funding.LocalDeltaMsat, funding.RemoteDeltaMsat),
                                    localView.Shift(funding.LocalDeltaMsat, funding.RemoteDeltaMsat), funding);

        if (p.MaxDustHtlcExposureMsat is { } maxDust)
        {
            CheckSendDustExposure(commitments, remoteView, htlc, maxDust, "B2-DUST-03");
            CheckSendDustExposure(commitments, localView, htlc, maxDust, "B2-DUST-04");
        }
    }

    /// <summary>The balance rules of an HTLC we offer (B2-ADD-S01..S04) on one funding's commitments.</summary>
    private static void ValidateSendAddBalances(CommitmentParams p, CommitmentView remoteView, CommitmentView localView,
                                                FundingView funding)
    {
        var reserve = funding.LocalReserveMsat;
        var on = funding.Label;
        if (p.LocalIsFunder)
        {
            foreach (var view in (CommitmentView[])[remoteView, localView])
            {
                var holderDust = p.Holder(view.Holder).DustLimitSatoshis;
                var spec = view.ToSpec();
                var baseFee = (long)CommitmentFeeCalculator.CommitmentBaseFeeSatoshis(spec, holderDust, p.Format)
                            * 1_000;
                if (view.LocalMsat - baseFee < reserve)
                    throw Refused("B2-ADD-S01",
                                  $"We could not pay the {view.Holder} commitment fee above our reserve after this HTLC{on}");
                if (view.LocalMsat - (long)CommitmentFeeCalculator.FunderCostMsat(spec, holderDust, p.Format)
                  < reserve)
                    throw Refused("B2-ADD-S02", $"We could not pay both anchors above our reserve after this HTLC{on}");
            }

            // Fee spike buffer: twice the feerate and one more non-dust HTLC, on both commitments (BOLT 2: "after adding
            // that HTLC to its commitment transaction"; LND applies it to both), each with its holder's dust limit.
            foreach (var view in (CommitmentView[])[remoteView, localView])
            {
                var spiked = view.ToSpec(checked(view.FeeratePerKw * 2));
                var spikeCost = (long)CommitmentFeeCalculator.FunderCostMsat(spiked,
                                                                             p.Holder(view.Holder).DustLimitSatoshis,
                                                                             p.Format)
                              + (long)(spiked.FeeratePerKw * (ulong)WeightConstants.HtlcOutputWeight / 1000) * 1_000;
                if (view.LocalMsat - spikeCost < reserve)
                    throw Refused("B2-ADD-S03",
                                  $"The HTLC would leave no fee spike buffer on the {view.Holder} commitment (2x feerate, one more HTLC){on}");
            }
        }
        else
        {
            // BOLT 2 sender: "MUST NOT offer amount_msat it cannot pay for ... while maintaining its channel reserve".
            if (remoteView.LocalMsat < reserve || localView.LocalMsat < reserve)
                throw Refused("B2-ADD-S01", $"The HTLC would take our balance below our channel reserve{on}");

            // BOLT 2: "the updated local or remote transaction". Both are needed: with different dust limits an HTLC
            // can be trimmed on one commitment and cost fee on the other.
            foreach (var view in (CommitmentView[])[remoteView, localView])
            {
                var funderCost = (long)CommitmentFeeCalculator.FunderCostMsat(view.ToSpec(),
                                                                     p.Holder(view.Holder).DustLimitSatoshis,
                                                                     p.Format);
                if (view.RemoteMsat - funderCost < funding.RemoteReserveMsat)
                    throw Refused("B2-ADD-S04",
                                  $"The funder could not pay the fee of the {view.Holder} commitment after this HTLC{on}");
            }
        }
    }

    /// <summary>
    /// Receiver rules for an HTLC the peer offers: B2-ADD-R01..R04 (R05: duplicate payment hashes are allowed; R07 is
    /// checked by the caller). With <see cref="CommitmentParams.HasInferredLimits"/> the checks against our announced
    /// limits and the peer's reserve are skipped (NL-194).
    /// </summary>
    public static void ValidateReceiveAdd(ChannelCommitments commitments, HtlcRecord htlc)
    {
        var p = commitments.Params;
        // Guessed limits (a migrated legacy channel) are not enforced as protocol rules: the peer negotiated the real
        // ones, and failing the channel over a guess would close a healthy channel.
        var enforceLimits = !p.HasInferredLimits;
        if (htlc.AmountMsat == 0 || (enforceLimits && htlc.AmountMsat < p.Local.HtlcMinimumMsat))
            throw Violation(commitments, "B2-ADD-R01",
                            $"amount_msat {htlc.AmountMsat} is 0 or below our htlc_minimum_msat {p.Local.HtlcMinimumMsat}");
        if (htlc.CltvExpiry >= ChannelCommitments.MaxCltvExpiry)
            throw Violation(commitments, "B2-ADD-R04", $"cltv_expiry {htlc.CltvExpiry} is not below 500000000");

        // BOLT 2 judges what the sender can afford, so our adds the peer has not signed yet are left out: it may have
        // offered this HTLC before it received them (crossed adds; found by the two-engine simulator).
        var localView = commitments.BuildProspectiveView(CommitmentSide.Local, htlc, peerView: true);
        var offered = localView.Htlcs.Where(h => h.Direction == HtlcDirection.Incoming).ToList();
        if (enforceLimits && offered.Count > p.Local.MaxAcceptedHtlcs)
            throw Violation(commitments, "B2-ADD-R03", $"More than our max_accepted_htlcs {p.Local.MaxAcceptedHtlcs}");
        var inFlight = offered.Aggregate(0UL, (sum, h) => checked(sum + h.AmountMsat));
        if (enforceLimits && inFlight > p.Local.MaxHtlcValueInFlightMsat)
            throw Violation(commitments, "B2-ADD-R03",
                            $"{inFlight} msat in flight exceeds our max_htlc_value_in_flight_msat {p.Local.MaxHtlcValueInFlightMsat}");

        // SP-OP-01 / SP-I6: affordable on every active funding (the peer's balance and reserve differ per funding)
        foreach (var funding in commitments.ActiveFundingViews())
        {
            var view = localView.Shift(funding.LocalDeltaMsat, funding.RemoteDeltaMsat);
            var cost = p.LocalIsFunder
                           ? 0
                           : (long)CommitmentFeeCalculator.FunderCostMsat(view.ToSpec(), p.Local.DustLimitSatoshis,
                                                                          p.Format);
            // The lenient reserve on spliced fundings (D9, Q3): Eclair keeps 1 % of the capacity there
            var remoteReserve = enforceLimits ? funding.RemoteReceiveReserveMsat : 0;
            if (view.RemoteMsat - cost < remoteReserve)
                throw Violation(commitments, "B2-ADD-R02",
                                $"The peer cannot afford this HTLC (and the fee it pays) above its channel reserve{funding.Label}");
        }
    }

    /// <summary>
    /// A fee update we send must be payable above our reserve on the peer's next commitment (the peer checks
    /// B2-FEE-R03).
    /// </summary>
    public static void ValidateSendFee(ChannelCommitments next)
    {
        var p = next.Params;
        var current = next.BuildProspectiveView(CommitmentSide.Remote, feerateOverride: next.LatestFeeratePerKw);
        foreach (var funding in next.ActiveFundingViews())
        {
            var view = current.Shift(funding.LocalDeltaMsat, funding.RemoteDeltaMsat);
            var cost = (long)CommitmentFeeCalculator.FunderCostMsat(view.ToSpec(), p.Remote.DustLimitSatoshis,
                                                                   p.Format);
            if (view.LocalMsat - cost < funding.LocalReserveMsat)
                throw Refused("B2-FEE-R03",
                              $"We could not pay feerate {next.LatestFeeratePerKw} above our reserve on the peer's commitment{funding.Label}");
        }
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
        var currentSpec = new CommitmentSpec(current.Holder, next.LatestFeeratePerKw, current.LocalMsat,
                                             current.RemoteMsat, current.Htlcs);
        foreach (var funding in next.ActiveFundingViews())
        {
            var spec = funding.Funding is { } f && !funding.IsCurrent
                           ? ChannelCommitments.SpecFor(currentSpec, f)
                           : currentSpec;
            var cost = (long)CommitmentFeeCalculator.FunderCostMsat(spec, p.Local.DustLimitSatoshis, p.Format);
            if ((long)spec.RemoteMsat - cost < 0)
                throw Violation(next, "B2-FEE-R03",
                                $"The funder cannot afford feerate {next.LatestFeeratePerKw} on our commitment{funding.Label}");
        }
    }

    /// <summary>
    /// The delayed affordability check at commit time: when the peer is the funder, the commitment its
    /// <c>commitment_signed</c> would give us must leave the funder enough to pay the fee (and anchors). The update-time
    /// checks are lenient on purpose (each one judges only what the peer knew when it sent it), so an add followed by an
    /// <c>update_fee</c> can pass both and still yield an unpayable commitment. BOLT 2 allows the fee check to wait
    /// "until the update_fee is committed"; this is that check, made before the signatures are verified or anything is
    /// revoked.
    /// </summary>
    /// <param name="current">The snapshot before the <c>commitment_signed</c>.</param>
    /// <param name="spec">The local commitment the <c>commitment_signed</c> covers.</param>
    public static void ValidateReceivedCommitFee(ChannelCommitments current, CommitmentSpec spec)
    {
        var p = current.Params;

        // SP-OP-01: on every active funding the peer signed this commitment for (the current funding's balances are
        // never negative: BuildSpec checks them)
        foreach (var funding in current.ActiveFundingViews().Where(f => !f.IsCurrent))
            if ((long)spec.LocalMsat + funding.LocalDeltaMsat < 0 || (long)spec.RemoteMsat + funding.RemoteDeltaMsat < 0)
                throw Violation(current, "B2-ADD-R02",
                                $"The commitment the peer signed has a negative balance{funding.Label}");

        if (p.LocalIsFunder)
            return;

        foreach (var funding in current.ActiveFundingViews())
        {
            var fundingSpec = funding.Funding is { } f && !funding.IsCurrent ? ChannelCommitments.SpecFor(spec, f) : spec;
            var cost = CommitmentFeeCalculator.FunderCostMsat(fundingSpec, p.Local.DustLimitSatoshis, p.Format);
            if (fundingSpec.RemoteMsat >= cost)
                continue;

            var requirementId = spec.FeeratePerKw != current.LocalCommit.Spec.FeeratePerKw
                                    ? "B2-FEE-R03"
                                    : "B2-ADD-R02";
            throw Violation(current, requirementId,
                            $"The funder cannot pay the {cost} msat fee of the commitment it signed (it holds {fundingSpec.RemoteMsat} msat){funding.Label}");
        }
    }

    private static void CheckSendDustExposure(ChannelCommitments commitments, CommitmentView view, HtlcRecord htlc,
                                              ulong maxDust, string requirementId)
    {
        var p = commitments.Params;
        var holderDust = p.Holder(view.Holder).DustLimitSatoshis;
        if (!CommitmentFeeCalculator.IsHtlcTrimmed(htlc.AmountMsat, htlc.IsOfferedBy(view.Holder), holderDust,
                                                   view.FeeratePerKw, p.Format))
            return;

        var exposure = CommitmentFeeCalculator.TrimmedHtlcTotalMsat(view.ToSpec(), holderDust, p.Format);
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