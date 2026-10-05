namespace NLightning.Domain.Channels.Quiescence;

using Commitments;
using Domain.Enums;
using Enums;
using Exceptions;
using Node;
using Protocol.Constants;
using ValueObjects;

/// <summary>
/// The pure rules of BOLT 2 "Channel Quiescence" (splicing plan §3.2, Q1-T2): when we may send <c>stfu</c>, what a
/// received <c>stfu</c> does, who the initiator is, and which peer messages break the protocol. The
/// <see cref="IQuiescenceService"/> implementation applies them under the channel's lock; nothing here keeps state.
/// </summary>
/// <remarks>
/// Requirement IDs (Q-W/Q-S/Q-R) are the rows of the splicing plan §1.1. Violations are returned, not thrown, so they
/// can be tested as tables; <see cref="CreateWarning"/> turns one into the <c>warning</c> + disconnect.
/// </remarks>
public static class QuiescenceRules
{
    /// <summary>
    /// Q-S-01: "MUST NOT send <c>stfu</c> unless <c>option_quiesce</c> is negotiated". <paramref name="negotiatedFeatures"/>
    /// are the features negotiated with the peer (either bit 34 or 35).
    /// </summary>
    public static bool IsNegotiated(FeatureSet negotiatedFeatures)
    {
        ArgumentNullException.ThrowIfNull(negotiatedFeatures);
        return negotiatedFeatures.HasFeature(Feature.OptionQuiesce);
    }

    /// <summary>
    /// Q-S-02: whether one of <b>our</b> HTLC additions, HTLC removals or fee updates is pending for either peer.
    /// Only the sender's own updates count: an add, removal or fee update of the peer's that is not yet committed or
    /// revoked does not block our <c>stfu</c>. A channel without an engine snapshot (<paramref name="commitments"/>
    /// null, NL-246) carries no update at all.
    /// </summary>
    public static bool HasPendingLocalUpdates(ChannelCommitments? commitments)
    {
        if (commitments is null)
            return false;

        return commitments.Htlcs.Values.Any(IsLocalUpdatePending)
            || commitments.FeeUpdates.Any(IsLocalFeeUpdatePending);
    }

    /// <summary>
    /// Whether <paramref name="htlc"/> carries an update of ours that is still pending for either peer:
    /// an HTLC we offered whose add is not yet irrevocably committed (states 10-13: not in both commitments or a
    /// previous commitment not revoked), or an HTLC the peer offered whose removal we sent and that is not final yet
    /// (states 35-38). The peer's adds (30-33) and the peer's removals of our HTLCs (15-18) are the peer's updates.
    /// </summary>
    public static bool IsLocalUpdatePending(HtlcRecord htlc)
    {
        ArgumentNullException.ThrowIfNull(htlc);
        return htlc.State is >= HtlcState.SentAddHtlc and < HtlcState.SentAddAckRevocation
                          or >= HtlcState.SentRemoveHtlc and < HtlcState.SentRemoveAckRevocation;
    }

    /// <summary>
    /// Whether <paramref name="feeUpdate"/> is an <c>update_fee</c> of ours (we are the funder, states 10-14) that is
    /// not final yet (not in both commitments with both previous commitments revoked). The peer's fee updates (we are
    /// the fundee) never block our <c>stfu</c>.
    /// </summary>
    public static bool IsLocalFeeUpdatePending(FeeUpdate feeUpdate)
    {
        ArgumentNullException.ThrowIfNull(feeUpdate);
        return feeUpdate.Owner == HtlcDirection.Outgoing && !feeUpdate.IsFinal;
    }

    /// <summary>
    /// Whether our <c>stfu</c> is owed: our request is queued (we will send <c>initiator</c> = 1) or the peer sent its
    /// <c>stfu</c> (we must reply with <c>initiator</c> = 0, Q-R-02), and we have not sent ours yet.
    /// </summary>
    public static bool IsStfuOwed(QuiescenceState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return !state.StfuSent && (state.PendingRequest.HasValue || state.StfuReceived);
    }

    /// <summary>
    /// Whether we may send our <c>stfu</c> now, checked in this order: Q-S-01 (negotiated), Q-S-03 (never twice), a
    /// <c>stfu</c> is owed at all, the link (channel <c>Open</c> and reestablished on this connection, decided by the
    /// caller), and Q-S-02 (none of our updates pending for either peer).
    /// </summary>
    /// <param name="quiesceNegotiated"><see cref="IsNegotiated"/> for the peer.</param>
    /// <param name="linkReady">The channel is <c>Open</c> and was reestablished (or opened) on this connection.</param>
    /// <param name="state">The channel's quiescence state.</param>
    /// <param name="commitments">The channel's engine snapshot (<c>ChannelModel.Commitments</c>).</param>
    public static StfuSendBlocker CheckSend(bool quiesceNegotiated, bool linkReady, QuiescenceState state,
                                            ChannelCommitments? commitments)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!quiesceNegotiated)
            return StfuSendBlocker.NotNegotiated;
        if (state.StfuSent)
            return StfuSendBlocker.AlreadySent;
        if (!IsStfuOwed(state))
            return StfuSendBlocker.NothingOwed;
        if (!linkReady)
            return StfuSendBlocker.LinkNotReady;

        return HasPendingLocalUpdates(commitments) ? StfuSendBlocker.LocalUpdatesPending : StfuSendBlocker.None;
    }

    /// <summary>
    /// Q-S-03: the <c>initiator</c> flag of the <c>stfu</c> we send now: "if it is replying to an <c>stfu</c>: MUST set
    /// <c>initiator</c> to 0; otherwise: MUST set <c>initiator</c> to 1".
    /// </summary>
    public static bool InitiatorFlagToSend(QuiescenceState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return !state.StfuReceived;
    }

    /// <summary>
    /// Records that we sent our <c>stfu</c> (with <see cref="InitiatorFlagToSend"/>). The sender "MUST now consider
    /// the channel to be quiescing" and "MUST NOT send an update message after <c>stfu</c>" (Q-S-04); if the peer's
    /// <c>stfu</c> was already received the channel is now quiescent (Q-R-01) and the initiator is resolved (Q-R-05).
    /// </summary>
    /// <param name="state">The state before sending.</param>
    /// <param name="localIsFunder">We are the channel funder (we sent <c>open_channel</c>).</param>
    /// <param name="now">The time the channel becomes quiescent, if it does.</param>
    /// <exception cref="InvalidOperationException">Our <c>stfu</c> was already sent (Q-S-03).</exception>
    public static QuiescenceState RecordSent(QuiescenceState state, bool localIsFunder, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.StfuSent)
            throw new InvalidOperationException("[Q-S-03] stfu was already sent on this channel");

        var next = state with { SentStfuInitiator = InitiatorFlagToSend(state) };
        return next.StfuReceived ? BecomeQuiescent(next, localIsFunder, now) : next;
    }

    /// <summary>
    /// Applies a received <c>stfu</c>. "The receiver of <c>stfu</c>: if it has sent <c>stfu</c> then: MUST now consider
    /// the channel to be quiescent" (Q-R-01); "otherwise: SHOULD NOT send any more update messages; MUST reply with
    /// <c>stfu</c> once it can do so" (Q-R-02, the returned state blocks our updates and owes our reply).
    /// </summary>
    /// <param name="state">The state before the message.</param>
    /// <param name="initiator">The received <c>initiator</c> flag.</param>
    /// <param name="quiesceNegotiated"><see cref="IsNegotiated"/> for the peer.</param>
    /// <param name="localIsFunder">We are the channel funder.</param>
    /// <param name="now">The time the channel becomes quiescent, if it does.</param>
    /// <returns>The next state, or the violation (<see cref="CreateWarning"/>).</returns>
    public static StfuReceiveResult Receive(QuiescenceState state, bool initiator, bool quiesceNegotiated,
                                            bool localIsFunder, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!quiesceNegotiated)
            return QuiescenceViolation.NotNegotiated;
        if (state.StfuReceived)
            return QuiescenceViolation.SecondStfu;
        if (!initiator && !state.StfuSent)
            return QuiescenceViolation.UnsolicitedReply;

        var next = state with { ReceivedStfuInitiator = initiator };
        return next.StfuSent ? BecomeQuiescent(next, localIsFunder, now) : next;
    }

    /// <summary>
    /// Who is the initiator once both <c>stfu</c> were exchanged: the side that sent <c>initiator</c> = 1. "If both
    /// sides send <c>stfu</c> simultaneously, they will both set <c>initiator</c> to 1, in which case the 'initiator'
    /// is arbitrarily considered to be the channel funder (the sender of <c>open_channel</c>)" (Q-R-05).
    /// </summary>
    /// <exception cref="ArgumentException">Both flags are 0 (nobody initiated; <see cref="Receive"/> rejects that as
    /// <see cref="QuiescenceViolation.UnsolicitedReply"/>).</exception>
    public static QuiescenceInitiator ResolveInitiator(bool sentInitiator, bool receivedInitiator, bool localIsFunder)
    {
        return (sentInitiator, receivedInitiator) switch
        {
            (true, false) => QuiescenceInitiator.Local,
            (false, true) => QuiescenceInitiator.Remote,
            (true, true) => localIsFunder ? QuiescenceInitiator.Local : QuiescenceInitiator.Remote,
            _ => throw new ArgumentException("Neither stfu set initiator = 1")
        };
    }

    /// <summary>
    /// The BOLT 2 update messages (<c>update_add_htlc</c>, <c>update_fulfill_htlc</c>, <c>update_fail_htlc</c>,
    /// <c>update_fail_malformed_htlc</c>, <c>update_fee</c>). <c>commitment_signed</c> and <c>revoke_and_ack</c> are
    /// not updates: they still flow while quiescing, so pending changes get committed and revoked.
    /// </summary>
    public static bool IsUpdateMessage(MessageTypes messageType) =>
        messageType is MessageTypes.UpdateAddHtlc or MessageTypes.UpdateFulfillHtlc or MessageTypes.UpdateFailHtlc
                    or MessageTypes.UpdateFailMalformedHtlc or MessageTypes.UpdateFee;

    /// <summary>
    /// Checks a message received from the peer against its own <c>stfu</c>: "MUST NOT send an update message after
    /// <c>stfu</c>" (Q-S-04). Returns <see cref="QuiescenceViolation.UpdateAfterStfu"/> for an update after the peer's
    /// <c>stfu</c>, else null.
    /// </summary>
    public static QuiescenceViolation? CheckPeerMessage(QuiescenceState state, MessageTypes messageType)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.StfuReceived && IsUpdateMessage(messageType) ? QuiescenceViolation.UpdateAfterStfu : null;
    }

    /// <summary>
    /// Whether we may propose a new update: not after our <c>stfu</c> (MUST NOT, Q-S-04), not after the peer's (SHOULD
    /// NOT, Q-R-02) and not while our own request is queued (the channel drains). Signing and revoking pending changes
    /// is always allowed.
    /// </summary>
    public static bool MayProposeUpdate(QuiescenceState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return !state.BlocksNewLocalUpdates;
    }

    /// <summary>
    /// The <c>warning</c> for a violation, scoped to the channel, closing the connection (the disconnection ends the
    /// quiescence, Q-R-04). The channel is not failed.
    /// </summary>
    public static ChannelWarningException CreateWarning(QuiescenceViolation violation, ChannelId channelId)
    {
        var (requirement, text) = violation switch
        {
            QuiescenceViolation.NotNegotiated => ("Q-S-01", "stfu without option_quiesce negotiated"),
            QuiescenceViolation.SecondStfu => ("Q-S-03", "stfu sent twice"),
            QuiescenceViolation.UnsolicitedReply => ("Q-S-03", "stfu with initiator=0 but we sent no stfu"),
            QuiescenceViolation.UpdateAfterStfu => ("Q-S-04", "update message after stfu"),
            _ => throw new ArgumentOutOfRangeException(nameof(violation), violation, "Unknown quiescence violation")
        };

        return new ChannelWarningException($"[{requirement}] {text} on channel {channelId}", channelId, text)
        {
            CloseConnection = true
        };
    }

    private static QuiescenceState BecomeQuiescent(QuiescenceState state, bool localIsFunder, DateTimeOffset now) =>
        state with
        {
            Initiator = ResolveInitiator(state.SentStfuInitiator!.Value, state.ReceivedStfuInitiator!.Value,
                                         localIsFunder),
            QuiescentSince = now
        };
}