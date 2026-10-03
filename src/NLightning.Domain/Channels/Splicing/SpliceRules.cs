namespace NLightning.Domain.Channels.Splicing;

using Bitcoin.ValueObjects;
using Commitments;
using Domain.Enums;
using Enums;
using Models;
using Node;
using Protocol.InteractiveTx;
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

    #region Splice RBF (wave SPR: tx_init_rbf / tx_ack_rbf on a pending splice)

    /// <summary>
    /// BOLT 2 splicing <c>tx_init_rbf</c>: "If there are more than 10 pending RBF attempts", the sender "MUST set a high
    /// enough <c>feerate</c> to ensure quick confirmation" and the receiver "SHOULD send <c>tx_abort</c>" when it is
    /// not.
    /// </summary>
    public const int MaxRbfAttemptsAtAnyFeerate = 10;

    /// <summary>
    /// SPR-T1 receiver, BOLT 2 "If another RBF attempt has been created recently: SHOULD send <c>tx_abort</c> ... and
    /// wait for the previous RBF attempt to confirm" (no number in the spec; NL-520). With
    /// <paramref name="minRbfInterval"/> set (<c>Splice:MinRbfInterval</c>, an operator override) the attempt is recent
    /// while younger than that wall-clock interval, and the block rule is not used. Otherwise (the default) it is recent
    /// until at least <paramref name="minRbfBlocks"/> new blocks (<c>Splice:MinRbfBlocks</c>, default 1) were processed
    /// since it was created: the attempt then had its chance to confirm, whatever the network's block interval.
    /// </summary>
    /// <param name="createdAtHeight">The tip when the previous attempt was stored (its broadcast row's
    /// <c>FirstBroadcastHeight</c>); null or 0 when unknown.</param>
    /// <param name="tipHeight">Our last processed block; null or 0 when unknown.</param>
    /// <param name="createdAt">When the previous attempt was stored; null when unknown.</param>
    /// <param name="now">The current time.</param>
    /// <param name="minRbfBlocks">New blocks needed before an RBF is no longer "recent"; 0 turns the block rule off.
    /// </param>
    /// <param name="minRbfInterval">The wall-clock override; null uses the block rule, <see cref="TimeSpan.Zero"/> turns
    /// the recency rule off.</param>
    /// <returns>Whether a peer's <c>tx_init_rbf</c> now gets <c>tx_abort</c> for recency. Unknown heights (no chain
    /// monitor, or an attempt stored without a height) never make an attempt recent.</returns>
    public static bool IsLastAttemptRecent(uint? createdAtHeight, uint? tipHeight, DateTimeOffset? createdAt,
                                           DateTimeOffset now, uint minRbfBlocks, TimeSpan? minRbfInterval)
    {
        if (minRbfInterval is { } interval)
            return interval > TimeSpan.Zero && createdAt is { } created && now - created < interval;

        if (minRbfBlocks == 0 || createdAtHeight is not (> 0 and var height) || tipHeight is not (> 0 and var tip))
            return false;

        return tip < (ulong)height + minRbfBlocks;
    }

    /// <summary>
    /// SPR-T1, the sender of a splice <c>tx_init_rbf</c> (BOLT 2 "Channel Splicing", <c>tx_init_rbf</c>, and
    /// interactive-tx IT-RBF-01): "MUST NOT send <c>tx_init_rbf</c> if the channel is not quiescent; if it is not the
    /// quiescence initiator"; "MAY send <c>tx_init_rbf</c> even if it is not the splice initiator" (so nothing is
    /// refused for that); "If there are more than 10 pending RBF attempts: MUST set a high enough <c>feerate</c>";
    /// "MUST NOT send <c>tx_init_rbf</c> if it has previously sent <c>splice_locked</c>"; "MUST NOT send
    /// <c>tx_init_rbf</c> if <c>option_zeroconf</c> has been negotiated"; the feerate at least
    /// max(floor(25/24 x previous), previous + 25) (<c>InteractiveTxRbfRules.GetMinimumNextFeerate</c>). Also refused:
    /// no pending splice to replace, a negotiation in progress, a batch above <c>ChannelCommitments.MaxActiveFundings</c>
    /// (SP-OP-04), our own cap <see cref="SpliceRbfConditions.MaxRbfAttempts"/> (<c>Splice:MaxRbfAttempts</c>) and a
    /// splice-out above our balance (SP-S-02). Every violation is <see cref="SpliceRuleAction.Refuse"/>.
    /// </summary>
    /// <param name="conditions">The channel and its pending splice, gathered under the lock.</param>
    /// <param name="feeratePerKw">The <c>tx_init_rbf.feerate</c> we would send.</param>
    /// <param name="contributionSatoshis">Our <c>funding_output_contribution</c> for the new attempt.</param>
    public static SpliceRuleViolation? CheckSendRbf(SpliceRbfConditions conditions, uint feeratePerKw,
                                                    long contributionSatoshis)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        var channel = conditions.Channel ?? throw new ArgumentException("No channel conditions", nameof(conditions));

        if (!channel.IsNegotiated)
            return Refuse("D14", "option_quiesce and option_splice are not both negotiated");
        if (!channel.IsQuiescent)
            return Refuse("SPR-T1", "the channel is not quiescent");
        if (!channel.LocalIsQuiescenceInitiator)
            return Refuse("SPR-T1", "we are not the quiescence initiator");
        if (conditions.ZeroconfNegotiated)
            return Refuse("SPR-T1", "option_zeroconf is negotiated: no splice RBF");
        if (conditions.LocalSentSpliceLocked)
            return Refuse("SP-LK-04", "we sent splice_locked");
        if (!channel.HasUnlockedSplice || conditions.PendingAttemptCount == 0)
            return Refuse("SPR-T1", "no pending splice to replace");
        if (channel.SpliceNegotiating)
            return Refuse("SPR-T1", "a splice negotiation is in progress");

        var minimum = InteractiveTxRbfRules.GetMinimumNextFeerate(conditions.LastAttemptFeeratePerKw);
        if (feeratePerKw < minimum)
            return Refuse("IT-RBF-01",
                          $"feerate {feeratePerKw} sat/kw is below {minimum} sat/kw (previous "
                        + $"{conditions.LastAttemptFeeratePerKw})");
        if (HasTooManyAttemptsForFeerate(conditions, feeratePerKw))
            return Refuse("SPR-T1",
                          $"{GetRbfAttemptCount(conditions)} RBF attempts are pending and {feeratePerKw} sat/kw does "
                        + "not ensure quick confirmation");
        if (WouldExceedBatch(conditions))
            return Refuse("SP-OP-04",
                          $"another attempt would exceed {ChannelCommitments.MaxActiveFundings} active fundings");
        if (GetRbfAttemptCount(conditions) >= conditions.MaxRbfAttempts)
            return Refuse("SPR-T2",
                          $"{GetRbfAttemptCount(conditions)} RBF attempts reach Splice:MaxRbfAttempts "
                        + $"({conditions.MaxRbfAttempts})");
        if (IsSpliceOutAboveBalance(contributionSatoshis, channel.LocalBalanceMsat))
            return Refuse("SP-S-02",
                          $"a contribution of {contributionSatoshis} sat is above our balance of "
                        + $"{channel.LocalBalanceMsat / 1_000} sat");

        return null;
    }

    /// <summary>
    /// SPR-T1, the receiver of a splice <c>tx_init_rbf</c>, in the BOLT 2 order. "MUST send a <c>warning</c> and close
    /// the connection or send an <c>error</c> and fail the channel" (we take <see cref="SpliceRuleAction.WarningAndClose"/>)
    /// when: the channel is not quiescent; the sender is not the quiescence initiator; the sender previously sent
    /// <c>splice_locked</c> (SP-LK-04 receive side, NL-489); <c>option_zeroconf</c> is negotiated; a negative
    /// <c>funding_output_contribution</c> is above the sender's current balance. <see cref="SpliceRuleAction.TxAbort"/>
    /// when: the feerate is below max(floor(25/24 x last), last + 25) (interactive-tx: "MUST respond with
    /// <c>tx_abort</c>"); "another RBF attempt has been created recently" (SHOULD); "more than 10 pending RBF attempts
    /// and the <c>feerate</c> is not high enough to ensure quick confirmation" (SHOULD); and, under "MAY send
    /// <c>tx_abort</c> for any reason", no pending splice to replace, a negotiation in progress, or a batch that would
    /// exceed <c>ChannelCommitments.MaxActiveFundings</c>. D14 (not negotiated) is a warning and close.
    /// </summary>
    /// <param name="conditions">The channel and its pending splice, gathered under the lock.</param>
    /// <param name="payload">The peer's <c>tx_init_rbf</c>.</param>
    /// <param name="contributionSatoshis">Its <c>funding_output_contribution</c> (null: not contributing, 0).</param>
    public static SpliceRuleViolation? CheckReceiveRbf(SpliceRbfConditions conditions, TxInitRbfPayload payload,
                                                       long? contributionSatoshis)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(payload);
        var channel = conditions.Channel ?? throw new ArgumentException("No channel conditions", nameof(conditions));

        // The MUSTs first (warning and close), then the tx_abort rows
        if (!channel.IsNegotiated)
            return WarnAndClose("D14", "tx_init_rbf on a splice without option_quiesce and option_splice negotiated");
        if (!channel.IsQuiescent)
            return WarnAndClose("SPR-T1", "tx_init_rbf on a channel that is not quiescent");
        if (channel.LocalIsQuiescenceInitiator)
            return WarnAndClose("SPR-T1", "tx_init_rbf from the node that is not the quiescence initiator");
        if (conditions.RemoteSentSpliceLocked)
            return WarnAndClose("SP-LK-04", "tx_init_rbf after the sender's splice_locked");
        if (conditions.ZeroconfNegotiated)
            return WarnAndClose("SPR-T1", "tx_init_rbf with option_zeroconf negotiated");
        if (IsSpliceOutAboveBalance(contributionSatoshis ?? 0, channel.RemoteBalanceMsat))
            return WarnAndClose("SPR-T1",
                                $"funding_output_contribution of {contributionSatoshis} sat is above the sender's "
                              + $"balance of {channel.RemoteBalanceMsat / 1_000} sat");

        if (!channel.HasUnlockedSplice || conditions.PendingAttemptCount == 0)
            return TxAbort("SPR-T1", "no pending splice to replace");
        if (channel.SpliceNegotiating)
            return TxAbort("SPR-T1", "a splice negotiation is in progress");
        var minimum = InteractiveTxRbfRules.GetMinimumNextFeerate(conditions.LastAttemptFeeratePerKw);
        if (payload.Feerate < minimum)
            return TxAbort("IT-RBF-01",
                           $"feerate {payload.Feerate} sat/kw is below {minimum} sat/kw (previous "
                         + $"{conditions.LastAttemptFeeratePerKw})");
        if (conditions.LastAttemptIsRecent)
            return TxAbort("SPR-T1", "another RBF attempt was created recently; waiting for it to confirm");
        if (HasTooManyAttemptsForFeerate(conditions, payload.Feerate))
            return TxAbort("SPR-T1",
                           $"{GetRbfAttemptCount(conditions)} RBF attempts are pending and {payload.Feerate} sat/kw "
                         + "does not ensure quick confirmation");
        if (WouldExceedBatch(conditions))
            return TxAbort("SP-OP-04",
                           $"another attempt would exceed {ChannelCommitments.MaxActiveFundings} active fundings");

        return null;
    }

    /// <summary>
    /// SPR-T1, the receiver of a splice <c>tx_ack_rbf</c>: a negative <c>funding_output_contribution</c> above the
    /// sender's current balance is a <c>warning</c> and close (BOLT 2); a <c>tx_ack_rbf</c> without our
    /// <c>tx_init_rbf</c> waiting for it is one too (as SP-R-02 for <c>splice_ack</c>).
    /// </summary>
    /// <param name="conditions">The channel and its pending splice, gathered under the lock.</param>
    /// <param name="contributionSatoshis">The peer's <c>funding_output_contribution</c> (null: not contributing).</param>
    /// <param name="rbfSent">Our <c>tx_init_rbf</c> waits for this answer.</param>
    public static SpliceRuleViolation? CheckReceiveAckRbf(SpliceRbfConditions conditions, long? contributionSatoshis,
                                                          bool rbfSent)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        var channel = conditions.Channel ?? throw new ArgumentException("No channel conditions", nameof(conditions));

        if (!rbfSent)
            return WarnAndClose("SPR-T1", "tx_ack_rbf without our tx_init_rbf");
        if (IsSpliceOutAboveBalance(contributionSatoshis ?? 0, channel.RemoteBalanceMsat))
            return WarnAndClose("SPR-T1",
                                $"funding_output_contribution of {contributionSatoshis} sat is above the sender's "
                              + $"balance of {channel.RemoteBalanceMsat / 1_000} sat");

        return null;
    }

    /// <summary>
    /// SPR-T1, the double-spend duty of an RBF attempt (interactive-tx IT-RBF-01: the new transaction "double-spends
    /// all other attempts"): for a splice it holds when the attempt's shared input (<c>shared_input_txid</c>, SP-TX-01)
    /// is the current funding output that every pending attempt of <paramref name="fundings"/> spends ("Since splice
    /// transactions always spend the current channel funding output, the RBF attempts automatically double-spend each
    /// other"); anything else is a <c>tx_abort</c>. The fee rule of an attempt (its total fee at least the previous
    /// attempt's, SP-TX-05) is <see cref="CheckTxComplete"/> with <see cref="SpliceTxCompleteFacts.PreviousAttemptFeeSatoshis"/>.
    /// </summary>
    /// <param name="sharedInputTxId">The attempt's shared input txid.</param>
    /// <param name="sharedInputVout">The attempt's shared input output index.</param>
    /// <param name="fundings">The channel's fundings with the pending splice the attempt replaces.</param>
    public static SpliceRuleViolation? CheckRbfDoubleSpends(TxId sharedInputTxId, uint sharedInputVout,
                                                            FundingSet fundings)
    {
        ArgumentNullException.ThrowIfNull(fundings);
        if (!fundings.HasPending)
            return TxAbort("IT-RBF-01", "no pending splice attempt to double-spend");
        if (sharedInputTxId != fundings.Current.FundingTxId || sharedInputVout != fundings.Current.OutputIndex)
            return TxAbort("IT-RBF-01",
                           $"the shared input {sharedInputTxId}:{sharedInputVout} is not the current funding output "
                         + $"{fundings.Current.FundingTxId}:{fundings.Current.OutputIndex} every pending attempt spends");

        return null;
    }

    /// <summary>The pending RBF attempts (the pending attempts besides the original splice).</summary>
    private static int GetRbfAttemptCount(SpliceRbfConditions conditions) =>
        Math.Max(0, conditions.PendingAttemptCount - 1);

    /// <summary>
    /// BOLT 2: "more than 10 pending RBF attempts" and a feerate below the quick-confirmation estimate (an unknown
    /// estimate counts as not quick).
    /// </summary>
    private static bool HasTooManyAttemptsForFeerate(SpliceRbfConditions conditions, uint feeratePerKw) =>
        GetRbfAttemptCount(conditions) > MaxRbfAttemptsAtAnyFeerate
     && (conditions.QuickConfirmationFeeratePerKw is not { } quick || feeratePerKw < quick);

    /// <summary>SP-OP-04: the new attempt makes the current funding plus every pending one exceed a batch of 20.</summary>
    private static bool WouldExceedBatch(SpliceRbfConditions conditions) =>
        conditions.PendingAttemptCount + 2 > ChannelCommitments.MaxActiveFundings;

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

        // A liquidity purchase (NL-850) moves its fee from the buyer's balance to the seller's on the new funding
        var newCapacity = facts.FundingOutputSatoshis;
        if (facts.LocalAddedOtherOutput
         && !KeepsReserve(facts.LocalBalanceMsat, facts.LocalContributionSatoshis, -facts.LiquidityFeeMsat,
                          facts.LocalReserveSatoshis, newCapacity))
            return TxAbort("SP-TX-05",
                           $"we take funds out but keep less than the reserve of "
                         + $"{GetReserveSatoshis(facts.LocalReserveSatoshis, newCapacity)} sat");
        if (facts.RemoteAddedOtherOutput
         && !KeepsReserve(facts.RemoteBalanceMsat, facts.RemoteContributionSatoshis, facts.LiquidityFeeMsat,
                          facts.RemoteReserveSatoshis, newCapacity))
            return TxAbort("SP-TX-05",
                           $"the peer takes funds out but keeps less than the reserve of "
                         + $"{GetReserveSatoshis(facts.RemoteReserveSatoshis, newCapacity)} sat");

        return facts.LiquidityFeeMsat switch
        {
            > 0 => CheckLiquidityFeeReserve(facts.LocalBalanceMsat, facts.LocalContributionSatoshis,
                                            (ulong)facts.LiquidityFeeMsat, facts.LocalReserveSatoshis, newCapacity,
                                            true),
            < 0 => CheckLiquidityFeeReserve(facts.RemoteBalanceMsat, facts.RemoteContributionSatoshis,
                                            (ulong)-facts.LiquidityFeeMsat, facts.RemoteReserveSatoshis, newCapacity,
                                            false),
            _ => null
        };
    }

    /// <summary>
    /// LA-RES-01 (liquidity ads, NL-850): the buyer of a purchase made with a splice pays its fee (mining plus service)
    /// from its balance on the new funding and must keep at least the reserve that matches the new capacity (D9) after
    /// paying it, so the fee never eats into the reserve. A violation is a <c>tx_abort</c> (BOLT 2: MAY send
    /// <c>tx_abort</c> for any reason); the seller checks it on the request, the buyer before it asks and on the answer,
    /// and both on the constructed transaction (<see cref="CheckTxComplete"/>).
    /// </summary>
    /// <param name="buyerBalanceMsat">The buyer's main balance on the current funding.</param>
    /// <param name="buyerContributionSatoshis">The buyer's signed contribution to the splice.</param>
    /// <param name="liquidityFeeMsat">The fee the buyer pays the seller.</param>
    /// <param name="announcedReserveSatoshis">The reserve the seller requires of the buyer.</param>
    /// <param name="newCapacitySatoshis">The new funding's capacity.</param>
    /// <param name="buyerIsLocal">Whether we are the buyer (only the message changes).</param>
    public static SpliceRuleViolation? CheckLiquidityFeeReserve(ulong buyerBalanceMsat, long buyerContributionSatoshis,
                                                                ulong liquidityFeeMsat, ulong announcedReserveSatoshis,
                                                                ulong newCapacitySatoshis, bool buyerIsLocal)
    {
        if (liquidityFeeMsat == 0
         || KeepsReserve(buyerBalanceMsat, buyerContributionSatoshis, -(Int128)liquidityFeeMsat,
                         announcedReserveSatoshis, newCapacitySatoshis))
            return null;

        var reserve = GetReserveSatoshis(announcedReserveSatoshis, newCapacitySatoshis);
        return TxAbort("LA-RES-01",
                       buyerIsLocal
                           ? $"we cannot pay the liquidity fee of {liquidityFeeMsat} msat and keep the reserve of "
                           + $"{reserve} sat"
                           : $"the buyer cannot pay the liquidity fee of {liquidityFeeMsat} msat and keep the "
                           + $"reserve of {reserve} sat");
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

    /// <param name="balanceMsat">The side's main balance on the current funding.</param>
    /// <param name="contributionSatoshis">Its signed contribution.</param>
    /// <param name="adjustmentMsat">What it gains (+) or pays (−) besides its contribution: a liquidity fee (NL-850).
    /// </param>
    /// <param name="announcedReserveSatoshis">The reserve the other side requires of it.</param>
    /// <param name="newCapacitySatoshis">The new funding's capacity.</param>
    private static bool KeepsReserve(ulong balanceMsat, long contributionSatoshis, Int128 adjustmentMsat,
                                     ulong announcedReserveSatoshis, ulong newCapacitySatoshis)
    {
        var after = (Int128)balanceMsat + (Int128)contributionSatoshis * 1_000 + adjustmentMsat;
        var reserveMsat = (Int128)GetReserveSatoshis(announcedReserveSatoshis, newCapacitySatoshis) * 1_000;
        return after >= 0 && after >= reserveMsat;
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
/// What the splice RBF rules (<see cref="SpliceRules.CheckSendRbf"/>, <see cref="SpliceRules.CheckReceiveRbf"/>,
/// <see cref="SpliceRules.CheckReceiveAckRbf"/>) judge, gathered under the channel's lock (wave SPR).
/// </summary>
/// <param name="Channel">The splice facts (<see cref="SpliceConditions.HasUnlockedSplice"/> = a pending splice to
/// replace; <see cref="SpliceConditions.SpliceNegotiating"/> = a splice or RBF negotiation in progress).</param>
/// <param name="PendingAttemptCount">The pending attempts of the splice (the original and its RBF siblings).</param>
/// <param name="LastAttemptFeeratePerKw">The feerate of the last successfully constructed attempt (IT-RBF-01).</param>
/// <param name="LocalSentSpliceLocked">We sent <c>splice_locked</c> for an attempt (SP-LK-04: no RBF after it).</param>
/// <param name="RemoteSentSpliceLocked">The peer sent <c>splice_locked</c> for an attempt (its <c>tx_init_rbf</c> is
/// then a warning and close).</param>
/// <param name="ZeroconfNegotiated"><c>option_zeroconf</c> is negotiated (no splice RBF at all).</param>
/// <param name="LastAttemptIsRecent">The last attempt was created "recently" by our policy
/// (<see cref="SpliceRules.IsLastAttemptRecent"/>; BOLT 2: SHOULD <c>tx_abort</c> a peer's RBF then).</param>
/// <param name="QuickConfirmationFeeratePerKw">The feerate our fee service deems "high enough to ensure quick
/// confirmation" (the next-block estimate), or null when unknown; used past
/// <see cref="SpliceRules.MaxRbfAttemptsAtAnyFeerate"/> attempts.</param>
/// <param name="MaxRbfAttempts">Our own cap on RBF attempts we start (<c>Splice:MaxRbfAttempts</c>, SPR-T2).</param>
public sealed record SpliceRbfConditions(
    SpliceConditions Channel,
    int PendingAttemptCount,
    uint LastAttemptFeeratePerKw,
    bool LocalSentSpliceLocked,
    bool RemoteSentSpliceLocked,
    bool ZeroconfNegotiated,
    bool LastAttemptIsRecent,
    uint? QuickConfirmationFeeratePerKw,
    int MaxRbfAttempts);

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
/// <param name="LiquidityFeeMsat">The fee of a liquidity purchase made with the splice (NL-850), signed from our point
/// of view: positive when we buy (it leaves our balance for the peer's), negative when we sell, 0 for none.</param>
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
    ulong? PreviousAttemptFeeSatoshis = null,
    long LiquidityFeeMsat = 0);