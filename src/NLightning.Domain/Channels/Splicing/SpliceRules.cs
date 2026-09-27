namespace NLightning.Domain.Channels.Splicing;

using Bitcoin.ValueObjects;
using Domain.Enums;
using Enums;
using Models;
using Node;
using Protocol.Payloads;

/// <summary>
/// The BOLT 2 splice rules as pure checks (splicing plan SP1-D-T1: SP-S-01/02, SP-R-01/02, SP-TX-01..05, D9, D14). Each
/// check returns null when the rule holds, else the violated requirement and the action BOLT 2 prescribes.
/// </summary>
/// <remarks>
/// <para>Requirement IDs are the rows of the splicing plan §1.3/§1.4. The checks run in the order the BOLT 2 text lists
/// them, so the first violated row is the one reported. For the "MUST send a <c>warning</c> and close the connection
/// or send an <c>error</c> and fail the channel" rows we always take the first option
/// (<see cref="SpliceRuleAction.WarningAndClose"/>): failing would force-close a healthy channel over a protocol slip,
/// as for quiescence.</para>
/// <para>The interactive-tx session (<c>InteractiveTxSession</c>) enforces SP-TX-01/02 and the structural half of
/// SP-TX-05 while the transaction is built; <see cref="CheckSharedInputAdd"/> and <see cref="CheckTxComplete"/> are the
/// same rules as tables, and the splice host runs <see cref="CheckTxComplete"/> on the constructed transaction again
/// (with the reserve and RBF rows the session does not know) before it signs anything.</para>
/// </remarks>
public static class SpliceRules
{
    /// <summary>The share of the new capacity that is the least reserve of a spliced channel (D9: 1 %).</summary>
    public const ulong ReserveCapacityDivisor = 100;

    #region D14

    /// <summary>D14: both <c>option_quiesce</c> (34/35) and <c>option_splice</c> (62/63) are negotiated.</summary>
    public static bool IsNegotiated(FeatureSet negotiatedFeatures)
    {
        ArgumentNullException.ThrowIfNull(negotiatedFeatures);
        return negotiatedFeatures.HasFeature(Feature.OptionQuiesce) && negotiatedFeatures.HasFeature(Feature.OptionSplice);
    }

    #endregion

    #region splice_init sender (SP-S-01, SP-S-02)

    /// <summary>
    /// SP-S-01/02: may we send <c>splice_init</c> with <paramref name="contributionSatoshis"/>? BOLT 2 (sending node):
    /// "MUST NOT send <c>splice_init</c> if the channel is not quiescent; if it is not the quiescence initiator; before
    /// sending and receiving <c>channel_ready</c>; while another splice is being negotiated; if another splice has been
    /// negotiated but <c>splice_locked</c> has not been sent and received; if it has previously sent <c>shutdown</c>",
    /// and a splice-out is "the amount that will be subtracted from its current channel balance" (so at most that
    /// balance). Every violation is <see cref="SpliceRuleAction.Refuse"/>: nothing is sent.
    /// </summary>
    public static SpliceRuleViolation? CheckSendInit(SpliceConditions conditions, long contributionSatoshis)
    {
        ArgumentNullException.ThrowIfNull(conditions);

        if (!conditions.IsNegotiated)
            return Refuse("D14", "option_quiesce and option_splice are not both negotiated");
        if (!conditions.IsQuiescent)
            return Refuse("SP-S-01", "the channel is not quiescent");
        if (!conditions.LocalIsQuiescenceInitiator)
            return Refuse("SP-S-01", "we are not the quiescence initiator");
        if (!conditions.ChannelReadyExchanged)
            return Refuse("SP-S-01", "channel_ready was not sent and received");
        if (conditions.SpliceNegotiating)
            return Refuse("SP-S-01", "another splice is being negotiated");
        if (conditions.HasUnlockedSplice)
            return Refuse("SP-S-01", "a negotiated splice is not locked yet");
        if (conditions.ShutdownSent)
            return Refuse("SP-S-01", "we sent shutdown");
        if (IsSpliceOutAboveBalance(contributionSatoshis, conditions.LocalBalanceMsat))
            return Refuse("SP-S-02",
                          $"a splice-out of {Magnitude(contributionSatoshis)} sat is above our balance of "
                        + $"{conditions.LocalBalanceMsat / 1_000} sat");

        return null;
    }

    #endregion

    #region splice_init receiver (SP-R-01)

    /// <summary>
    /// SP-R-01: the peer's <c>splice_init</c> (<paramref name="feerateAcceptable"/>: our feerate policy accepts its
    /// <c>funding_feerate_perkw</c>, else <c>tx_abort</c>). BOLT 2 (receiving node), in order: not quiescent; the
    /// sender is not the quiescence initiator; another splice being negotiated; another splice negotiated but not
    /// locked; <c>shutdown</c> received: <c>warning</c> and close; an unacceptable feerate: <c>tx_abort</c>; a negative
    /// contribution above the sender's current balance: <c>warning</c> and close. D14: a peer that splices without
    /// both features negotiated breaks the protocol too.
    /// </summary>
    public static SpliceRuleViolation? CheckReceiveInit(SpliceConditions conditions, SpliceInitPayload payload,
                                                        bool feerateAcceptable)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(payload);

        if (!conditions.IsNegotiated)
            return WarnAndClose("D14", "splice_init without option_quiesce and option_splice negotiated");
        if (!conditions.IsQuiescent)
            return WarnAndClose("SP-R-01", "splice_init on a channel that is not quiescent");
        if (conditions.LocalIsQuiescenceInitiator)
            return WarnAndClose("SP-R-01", "splice_init from the node that is not the quiescence initiator");
        if (conditions.SpliceNegotiating)
            return WarnAndClose("SP-R-01", "splice_init while another splice is being negotiated");
        if (conditions.HasUnlockedSplice)
            return WarnAndClose("SP-R-01", "splice_init while a negotiated splice is not locked");
        if (conditions.ShutdownReceived)
            return WarnAndClose("SP-R-01", "splice_init after shutdown");
        if (!feerateAcceptable)
            return new SpliceRuleViolation("SP-R-01", SpliceRuleAction.TxAbort,
                                           $"funding_feerate_perkw {payload.FundingFeeratePerKw} is not acceptable");
        if (IsSpliceOutAboveBalance(payload.FundingContributionSatoshis, conditions.RemoteBalanceMsat))
            return WarnAndClose("SP-R-01",
                                $"splice-out of {Magnitude(payload.FundingContributionSatoshis)} sat above the sender's "
                              + $"balance of {conditions.RemoteBalanceMsat / 1_000} sat");

        return null;
    }

    #endregion

    #region splice_ack receiver (SP-R-02)

    /// <summary>
    /// SP-R-02: the peer's <c>splice_ack</c> (<paramref name="initSent"/>: our <c>splice_init</c> waits for it). BOLT 2
    /// (receiving node): without our <c>splice_init</c>, <c>warning</c> and close; a negative contribution above the
    /// sender's current balance, <c>warning</c> and close.
    /// </summary>
    public static SpliceRuleViolation? CheckReceiveAck(SpliceConditions conditions, SpliceAckPayload payload,
                                                       bool initSent)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(payload);

        if (!initSent)
            return WarnAndClose("SP-R-02", "splice_ack without our splice_init");
        if (IsSpliceOutAboveBalance(payload.FundingContributionSatoshis, conditions.RemoteBalanceMsat))
            return WarnAndClose("SP-R-02",
                                $"splice-out of {Magnitude(payload.FundingContributionSatoshis)} sat above the sender's "
                              + $"balance of {conditions.RemoteBalanceMsat / 1_000} sat");

        return null;
    }

    #endregion

    #region Transaction construction (SP-TX-01..05)

    /// <summary>
    /// SP-TX-01/02: a <c>tx_add_input</c> that carries <c>shared_input_txid</c>. BOLT 2: the splice initiator adds the
    /// current funding output "with <c>shared_input_txid</c> containing the txid of the previous funding transaction",
    /// "MUST NOT include <c>prevtx</c>" and "MUST set <c>prevtx_vout</c> to the previous funding output index"; the
    /// receiver "MUST fail the negotiation by sending <c>tx_abort</c>" when the txid or the index does not match.
    /// </summary>
    /// <param name="senderIsSpliceInitiator">The sender of the <c>tx_add_input</c> is the splice initiator.</param>
    /// <param name="sharedInputTxId">Its <c>shared_input_txid</c>.</param>
    /// <param name="prevTxVout">Its <c>prevtx_vout</c>.</param>
    /// <param name="prevTxLength">Its <c>prevtx_len</c>.</param>
    /// <param name="currentFundingTxId">The channel's current funding txid.</param>
    /// <param name="currentFundingOutputIndex">The channel's current funding output index.</param>
    public static SpliceRuleViolation? CheckSharedInputAdd(bool senderIsSpliceInitiator, TxId sharedInputTxId,
                                                           uint prevTxVout, int prevTxLength, TxId currentFundingTxId,
                                                           uint currentFundingOutputIndex)
    {
        if (!senderIsSpliceInitiator)
            return TxAbort("SP-TX-01", "the shared input was added by the splice non-initiator");
        if (prevTxLength != 0)
            return TxAbort("SP-TX-01", "the shared input carries a prevtx");
        if (sharedInputTxId != currentFundingTxId)
            return TxAbort("SP-TX-02", "shared_input_txid is not the current funding txid");
        if (prevTxVout != currentFundingOutputIndex)
            return TxAbort("SP-TX-02",
                           $"prevtx_vout {prevTxVout} is not the funding output index {currentFundingOutputIndex}");

        return null;
    }

    /// <summary>
    /// SP-TX-03: the new funding output's amount. BOLT 2: "the previous channel capacity with the
    /// <c>funding_contribution_satoshis</c>s from <c>splice_init</c> and <c>splice_ack</c> applied".
    /// </summary>
    /// <returns>The new capacity, or null when the contributions leave nothing (or overflow).</returns>
    public static ulong? GetNewCapacitySatoshis(ulong previousCapacitySatoshis, long initContributionSatoshis,
                                                long ackContributionSatoshis)
    {
        var capacity = (Int128)previousCapacitySatoshis + initContributionSatoshis + ackContributionSatoshis;
        return capacity <= 0 || capacity > ulong.MaxValue ? null : (ulong)capacity;
    }

    /// <summary>
    /// SP-TX-03: the funding output the splice initiator added pays exactly
    /// <see cref="GetNewCapacitySatoshis"/>; any other amount (or contributions that leave nothing) is a
    /// <c>tx_abort</c> (BOLT 2 <c>tx_complete</c>: "There is not exactly one channel funding output using the funding
    /// public keys and funding contributions").
    /// </summary>
    public static SpliceRuleViolation? CheckFundingOutputAmount(ulong previousCapacitySatoshis,
                                                                long initContributionSatoshis,
                                                                long ackContributionSatoshis,
                                                                ulong fundingOutputSatoshis)
    {
        var expected = GetNewCapacitySatoshis(previousCapacitySatoshis, initContributionSatoshis,
                                              ackContributionSatoshis);
        if (expected is null)
            return TxAbort("SP-TX-03", "the contributions leave no channel capacity");

        return expected.Value == fundingOutputSatoshis
                   ? null
                   : TxAbort("SP-TX-03",
                             $"the funding output pays {fundingOutputSatoshis} sat, not {expected.Value} sat");
    }

    /// <summary>
    /// SP-TX-04: with <c>require_confirmed_inputs</c> sent in <c>splice_init</c>/<c>splice_ack</c>, the sender "MUST
    /// NOT send a <c>tx_add_input</c> that contains an unconfirmed input" (the interactive-tx receiver fails the
    /// negotiation with <c>tx_abort</c>).
    /// </summary>
    public static SpliceRuleViolation? CheckInputConfirmation(bool confirmedInputsRequired, bool inputConfirmed) =>
        confirmedInputsRequired && !inputConfirmed
            ? TxAbort("SP-TX-04", "an unconfirmed input was added although require_confirmed_inputs was sent")
            : null;

    /// <summary>
    /// D9 (inferred): the reserve a side must keep on a spliced channel is the larger of the announced reserve and
    /// 1 % of the new capacity (BOLT 2: "the channel reserve that matches the new channel capacity").
    /// </summary>
    public static ulong GetReserveSatoshis(ulong announcedReserveSatoshis, ulong newCapacitySatoshis) =>
        Math.Max(announcedReserveSatoshis, newCapacitySatoshis / ReserveCapacityDivisor);

    /// <summary>
    /// SP-TX-05, the <c>tx_complete</c> receiver's splice checks, in the BOLT 2 order: "There is not exactly one input
    /// spending the current funding transaction. There is not exactly one channel funding output using the funding
    /// public keys and funding contributions from <c>splice_init</c> and <c>splice_ack</c>. This is an RBF attempt and
    /// the transaction's total fees is less than the last successfully negotiated splice transaction's fees. Either
    /// side has added an output other than the channel funding output and the balance for that side is less than the
    /// channel reserve that matches the new channel capacity." Every violation is a <c>tx_abort</c>.
    /// </summary>
    public static SpliceRuleViolation? CheckTxComplete(SpliceTxCompleteFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.SharedInputCount != 1)
            return TxAbort("SP-TX-05",
                           $"{facts.SharedInputCount} inputs spend the current funding output, not exactly one");
        if (facts.FundingOutputCount != 1)
            return TxAbort("SP-TX-05", $"{facts.FundingOutputCount} channel funding outputs, not exactly one");
        if (CheckFundingOutputAmount(facts.PreviousCapacitySatoshis, facts.LocalContributionSatoshis,
                                     facts.RemoteContributionSatoshis, facts.FundingOutputSatoshis) is { } amount)
            return amount with { RequirementId = "SP-TX-05" };
        if (facts.PreviousAttemptFeeSatoshis is { } previousFee && facts.TotalFeeSatoshis < previousFee)
            return TxAbort("SP-TX-05",
                           $"the RBF attempt pays {facts.TotalFeeSatoshis} sat, less than the previous "
                         + $"{previousFee} sat");

        var newCapacity = facts.FundingOutputSatoshis;
        if (facts.LocalAddedOtherOutput
         && !KeepsReserve(facts.LocalBalanceMsat, facts.LocalContributionSatoshis, facts.LocalReserveSatoshis,
                          newCapacity))
            return TxAbort("SP-TX-05",
                           $"we take funds out but keep less than the reserve of "
                         + $"{GetReserveSatoshis(facts.LocalReserveSatoshis, newCapacity)} sat");
        if (facts.RemoteAddedOtherOutput
         && !KeepsReserve(facts.RemoteBalanceMsat, facts.RemoteContributionSatoshis, facts.RemoteReserveSatoshis,
                          newCapacity))
            return TxAbort("SP-TX-05",
                           $"the peer takes funds out but keeps less than the reserve of "
                         + $"{GetReserveSatoshis(facts.RemoteReserveSatoshis, newCapacity)} sat");

        return null;
    }

    /// <summary>
    /// SP-TX-05: a side's balance on the new funding (BOLT 2: "adding their respective
    /// <c>funding_contribution_satoshis</c> to their previous channel balance"), or null when it would be negative.
    /// </summary>
    public static ulong? GetBalanceAfterMsat(ulong previousBalanceMsat, long contributionSatoshis)
    {
        var balance = (Int128)previousBalanceMsat + (Int128)contributionSatoshis * 1_000;
        return balance < 0 || balance > ulong.MaxValue ? null : (ulong)balance;
    }

    #endregion

    private static bool KeepsReserve(ulong balanceMsat, long contributionSatoshis, ulong announcedReserveSatoshis,
                                     ulong newCapacitySatoshis)
    {
        var after = GetBalanceAfterMsat(balanceMsat, contributionSatoshis);
        var reserveMsat = (Int128)GetReserveSatoshis(announcedReserveSatoshis, newCapacitySatoshis) * 1_000;
        return after is { } value && value >= reserveMsat;
    }

    private static bool IsSpliceOutAboveBalance(long contributionSatoshis, ulong balanceMsat) =>
        contributionSatoshis < 0 && (Int128)Magnitude(contributionSatoshis) * 1_000 > balanceMsat;

    private static ulong Magnitude(long value) => value == long.MinValue ? 1UL << 63 : (ulong)Math.Abs(value);

    private static SpliceRuleViolation Refuse(string requirementId, string reason) =>
        new(requirementId, SpliceRuleAction.Refuse, reason);

    private static SpliceRuleViolation WarnAndClose(string requirementId, string reason) =>
        new(requirementId, SpliceRuleAction.WarningAndClose, reason);

    private static SpliceRuleViolation TxAbort(string requirementId, string reason) =>
        new(requirementId, SpliceRuleAction.TxAbort, reason);
}

/// <summary>
/// What <see cref="SpliceRules.CheckTxComplete"/> judges about a constructed splice transaction (SP-TX-05), from our
/// point of view.
/// </summary>
/// <param name="SharedInputCount">Inputs spending the current funding output.</param>
/// <param name="FundingOutputCount">Outputs paying the new funding script.</param>
/// <param name="FundingOutputSatoshis">The new funding output's amount (0 when there is none).</param>
/// <param name="PreviousCapacitySatoshis">The current funding's capacity.</param>
/// <param name="LocalContributionSatoshis">Our signed contribution (<c>splice_init</c> or <c>splice_ack</c>).</param>
/// <param name="RemoteContributionSatoshis">The peer's signed contribution.</param>
/// <param name="LocalBalanceMsat">Our current main balance.</param>
/// <param name="RemoteBalanceMsat">The peer's current main balance.</param>
/// <param name="LocalReserveSatoshis">The reserve the peer requires of us (announced).</param>
/// <param name="RemoteReserveSatoshis">The reserve we require of the peer (announced).</param>
/// <param name="LocalAddedOtherOutput">We added an output other than the funding output.</param>
/// <param name="RemoteAddedOtherOutput">The peer added an output other than the funding output.</param>
/// <param name="TotalFeeSatoshis">The transaction's total fee (inputs minus outputs).</param>
/// <param name="PreviousAttemptFeeSatoshis">For an RBF attempt, the last negotiated attempt's total fee; null
/// otherwise.</param>
public sealed record SpliceTxCompleteFacts(
    int SharedInputCount,
    int FundingOutputCount,
    ulong FundingOutputSatoshis,
    ulong PreviousCapacitySatoshis,
    long LocalContributionSatoshis,
    long RemoteContributionSatoshis,
    ulong LocalBalanceMsat,
    ulong RemoteBalanceMsat,
    ulong LocalReserveSatoshis,
    ulong RemoteReserveSatoshis,
    bool LocalAddedOtherOutput,
    bool RemoteAddedOtherOutput,
    ulong TotalFeeSatoshis,
    ulong? PreviousAttemptFeeSatoshis = null);