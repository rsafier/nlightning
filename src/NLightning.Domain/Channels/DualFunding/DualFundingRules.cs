namespace NLightning.Domain.Channels.DualFunding;

using Bitcoin.ValueObjects;
using Domain.Enums;
using Money;
using Node;
using Protocol.InteractiveTx;
using Protocol.InteractiveTx.Models;

/// <summary>
/// The BOLT 2 "Channel Establishment v2" rules that are not the interactive-tx engine's (splicing plan wave DF, DF1/DF2):
/// the fixed channel reserve, the opener's fee weight, the zero-HTLC first commitment and the RBF of an unconfirmed
/// open. Pure: no I/O, no clock.
/// </summary>
public static class DualFundingRules
{
    /// <summary>The <c>locktime</c> we refuse in a peer's <c>open_channel2</c>: at or above the timestamp threshold
    /// (500,000,000) it is a time, which we never use for a funding transaction.</summary>
    public const uint LocktimeThreshold = 500_000_000;

    /// <summary>
    /// BOLT 2 (open_channel2 rationale): "the channel reserve is fixed at 1% of the total channel balance
    /// (<c>open_channel2.funding_satoshis</c> + <c>accept_channel2.funding_satoshis</c>) rounded down to the nearest
    /// whole satoshi or the <c>dust_limit_satoshis</c>, whichever is greater".
    /// </summary>
    /// <param name="totalFunding">Both contributions.</param>
    /// <param name="dustLimit">The dust limit the reserve must not fall below.</param>
    public static LightningMoney GetChannelReserve(LightningMoney totalFunding, LightningMoney dustLimit)
    {
        ArgumentNullException.ThrowIfNull(totalFunding);
        ArgumentNullException.ThrowIfNull(dustLimit);

        var onePercent = LightningMoney.Satoshis(totalFunding.Satoshi / 100);
        return onePercent > dustLimit ? onePercent : dustLimit;
    }

    /// <summary>
    /// The weight the opener pays besides its own inputs and outputs (IT-S-03): the common fields and the channel's
    /// funding output, which the opener MUST add (BOLT 2 "Funding Composition").
    /// </summary>
    public static int GetOpenerExtraWeight(BitcoinScript fundingScript) =>
        CollaborativeFeeCalculator.CommonFieldsWeight + (int)CollaborativeFeeCalculator.OutputWeight(fundingScript);

    /// <summary>
    /// BOLT 2 (<c>commitment_signed</c> of a v2 open): "if the message has one or more HTLCs: MUST fail the
    /// negotiation". Returns the violation, or null.
    /// </summary>
    public static string? CheckFirstCommitmentSigned(int htlcSignatureCount) =>
        htlcSignatureCount == 0
            ? null
            : $"[DF-CS-01] the first commitment_signed of a dual-funded open carries {htlcSignatureCount} HTLC signatures";

    /// <summary>
    /// The receiver checks of <c>open_channel2</c> that the v1 validator does not make (BOLT 2 "The open_channel2
    /// Message"): <c>channel_type</c> set, <c>commitment_feerate_perkw</c> 0 exactly with <c>zero_fee_commitments</c>,
    /// a block-height <c>locktime</c> and a funding feerate from the relay floor. Returns the violation, or null.
    /// </summary>
    /// <param name="channelType">The <c>channel_type</c> TLV's features, or null when it is missing.</param>
    /// <param name="fundingFeeratePerKw">The opener's <c>funding_feerate_perkw</c>.</param>
    /// <param name="commitmentFeeratePerKw">The opener's <c>commitment_feerate_perkw</c>.</param>
    /// <param name="locktime">The funding transaction's <c>locktime</c>.</param>
    /// <param name="minimumFeeratePerKw">The lowest funding feerate we accept (BOLT 3's 253 sat/kw floor).</param>
    public static string? CheckOpenChannel2(FeatureSet? channelType, uint fundingFeeratePerKw,
                                            uint commitmentFeeratePerKw, uint locktime, uint minimumFeeratePerKw)
    {
        if (channelType is null)
            return "[DF-OPEN-01] channel_type is not set";

        var zeroFee = channelType.IsFeatureSet(Feature.ZeroFeeCommitments, true);
        if (zeroFee && commitmentFeeratePerKw != 0)
            return "[DF-OPEN-02] zero_fee_commitments with a commitment_feerate_perkw that is not 0";

        if (locktime >= LocktimeThreshold)
            return $"[DF-OPEN-03] locktime {locktime} is a timestamp";

        return fundingFeeratePerKw < minimumFeeratePerKw
                   ? $"[DF-OPEN-04] funding_feerate_perkw {fundingFeeratePerKw} is below {minimumFeeratePerKw}"
                   : null;
    }

    /// <summary>
    /// BOLT 2 (<c>tx_init_rbf</c>): the sender and the recipient MUST NOT have sent or received <c>channel_ready</c>.
    /// </summary>
    public static bool CanRbf(bool channelReadySentOrReceived) => !channelReadySentOrReceived;

    /// <summary>
    /// Our contribution to an RBF attempt of a dual-funded open: the same wallet inputs as the previous attempt (so the
    /// new transaction double-spends it, IT-RBF-01) and the same share of the funding output, with the change lowered
    /// to pay for our weight at <paramref name="feeratePerKw"/>. The change is dropped when it would fall below
    /// <paramref name="dustLimit"/> (the surplus goes to fees).
    /// </summary>
    /// <param name="previous">Our contribution to the previous attempt.</param>
    /// <param name="fundingShare">Our share of the funding output.</param>
    /// <param name="isInitiator">Whether we are the initiator of the new attempt (we then pay the common fields and the
    /// funding output).</param>
    /// <param name="sharedFunding">The funding output of the new attempt.</param>
    /// <param name="feeratePerKw">The new attempt's feerate.</param>
    /// <param name="dustLimit">The smallest change output kept.</param>
    /// <returns>The new contribution, or null when the previous inputs cannot pay for it.</returns>
    public static InteractiveTxContribution? RebuildContributionForFeerate(InteractiveTxContribution previous,
                                                                          LightningMoney fundingShare,
                                                                          bool isInitiator,
                                                                          SharedFundingSpec sharedFunding,
                                                                          uint feeratePerKw, LightningMoney dustLimit)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(fundingShare);
        ArgumentNullException.ThrowIfNull(sharedFunding);

        var inputTotal = previous.Inputs.Aggregate(0UL, (sum, i) => sum + i.Amount.MilliSatoshi);
        var fixedOutputs = previous.Outputs.Where(o => !o.IsChange).ToList();
        var fixedTotal = fixedOutputs.Aggregate(0UL, (sum, o) => sum + o.Amount.MilliSatoshi);
        var changeScript = previous.Outputs.FirstOrDefault(o => o.IsChange)?.ScriptPubKey;

        // With the change output first: what is left for it after our share, the fixed outputs and our fee
        if (changeScript is { } script)
        {
            var withChange = previous with { Outputs = [.. fixedOutputs, new ContributedOutput(dustLimit, script, true)] };
            var fee = CollaborativeFeeCalculator.GetLocalContributionFee(withChange, isInitiator, sharedFunding,
                                                                         feeratePerKw);
            var spent = fundingShare.MilliSatoshi + fixedTotal + fee.MilliSatoshi;
            if (inputTotal >= spent)
            {
                var change = LightningMoney.Satoshis((inputTotal - spent) / 1_000);
                if (change >= dustLimit)
                    return previous with
                    {
                        Outputs = [.. fixedOutputs, new ContributedOutput(change, script, true)]
                    };
            }
        }

        // Without change: the inputs must still pay for our share, the fixed outputs and our fee
        var withoutChange = previous with { Outputs = fixedOutputs };
        var feeWithoutChange = CollaborativeFeeCalculator.GetLocalContributionFee(withoutChange, isInitiator,
                                                                                   sharedFunding, feeratePerKw);
        return inputTotal >= fundingShare.MilliSatoshi + fixedTotal + feeWithoutChange.MilliSatoshi
                   ? withoutChange
                   : null;
    }
}