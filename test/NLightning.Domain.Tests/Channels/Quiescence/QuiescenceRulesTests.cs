namespace NLightning.Domain.Tests.Channels.Quiescence;

using Commitments;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Quiescence;
using Domain.Channels.ValueObjects;
using Domain.Enums;
using Domain.Node;
using Domain.Protocol.Constants;

/// <summary>
/// Splicing plan Q1-T2: the pure BOLT 2 "Channel Quiescence" rules. Each region is one requirement row of the plan
/// (§1.1) and quotes the spec line it proves.
/// </summary>
public class QuiescenceRulesTests
{
    private static readonly DateTimeOffset s_now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0x42, 32).ToArray());

    private static readonly QuiescenceState s_requested = QuiescenceState.None with
    {
        PendingRequest = QuiescencePurpose.Splice
    };

    #region Q-S-01 "MUST NOT send stfu unless option_quiesce is negotiated"

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void Given_NegotiatedBits_When_IsNegotiated_Then_TrueForEitherQuiesceBit(bool optional, bool compulsory,
                                                                                    bool expected)
    {
        // Arrange
        var features = new FeatureSet();
        if (optional)
            features.SetFeature(Feature.OptionQuiesce, false);
        if (compulsory)
            features.SetFeature(Feature.OptionQuiesce, true);

        // Act
        var negotiated = QuiescenceRules.IsNegotiated(features);

        // Assert
        Assert.Equal(expected, negotiated);
    }

    [Fact]
    public void Given_QuiesceNotNegotiated_When_CheckSend_Then_NotNegotiated()
    {
        // Act
        var blocker = QuiescenceRules.CheckSend(false, true, s_requested, CommitmentsTestKit.Create(100_000, 0));

        // Assert
        Assert.Equal(StfuSendBlocker.NotNegotiated, blocker);
    }

    [Fact]
    public void Given_QuiesceNotNegotiated_When_StfuReceived_Then_ViolationAndStateUnchanged()
    {
        // Act
        var result = QuiescenceRules.Receive(QuiescenceState.None, true, false, true, s_now);

        // Assert
        Assert.Equal(QuiescenceViolation.NotNegotiated, result.Violation);
    }

    #endregion

    #region Q-S-02 "MUST NOT send stfu if any of the sender's htlc additions, htlc removals or fee updates are pending for either peer"

    [Theory]
    // Our adds: pending until irrevocably committed in both commitments (14)
    [InlineData(HtlcDirection.Outgoing, HtlcState.SentAddHtlc, true)]
    [InlineData(HtlcDirection.Outgoing, HtlcState.SentAddCommit, true)]
    [InlineData(HtlcDirection.Outgoing, HtlcState.RcvdAddRevocation, true)]
    [InlineData(HtlcDirection.Outgoing, HtlcState.RcvdAddAckCommit, true)]
    [InlineData(HtlcDirection.Outgoing, HtlcState.SentAddAckRevocation, false)]
    // The peer's removals of our HTLCs are the peer's updates
    [InlineData(HtlcDirection.Outgoing, HtlcState.RcvdRemoveHtlc, false)]
    [InlineData(HtlcDirection.Outgoing, HtlcState.RcvdRemoveCommit, false)]
    [InlineData(HtlcDirection.Outgoing, HtlcState.SentRemoveRevocation, false)]
    [InlineData(HtlcDirection.Outgoing, HtlcState.SentRemoveAckCommit, false)]
    [InlineData(HtlcDirection.Outgoing, HtlcState.RcvdRemoveAckRevocation, false)]
    // The peer's adds (received, even unrevoked) never block our stfu
    [InlineData(HtlcDirection.Incoming, HtlcState.RcvdAddHtlc, false)]
    [InlineData(HtlcDirection.Incoming, HtlcState.RcvdAddCommit, false)]
    [InlineData(HtlcDirection.Incoming, HtlcState.SentAddRevocation, false)]
    [InlineData(HtlcDirection.Incoming, HtlcState.SentAddAckCommit, false)]
    [InlineData(HtlcDirection.Incoming, HtlcState.RcvdAddAckRevocation, false)]
    // Our removals of the peer's HTLCs: pending until final (39)
    [InlineData(HtlcDirection.Incoming, HtlcState.SentRemoveHtlc, true)]
    [InlineData(HtlcDirection.Incoming, HtlcState.SentRemoveCommit, true)]
    [InlineData(HtlcDirection.Incoming, HtlcState.RcvdRemoveRevocation, true)]
    [InlineData(HtlcDirection.Incoming, HtlcState.RcvdRemoveAckCommit, true)]
    [InlineData(HtlcDirection.Incoming, HtlcState.SentRemoveAckRevocation, false)]
    public void Given_HtlcState_When_IsLocalUpdatePending_Then_OnlyOurUnfinishedUpdatesCount(
        HtlcDirection direction, HtlcState state, bool expected)
    {
        // Arrange
        var removal = HtlcStateTable.IsRemoval(state) ? HtlcRemoval.Fail(new byte[] { 1 }) : null;
        var htlc = new HtlcRecord(direction, 0, 1_000_000, CommitmentsTestKit.PaymentHash(1), 600, state, removal);

        // Act
        var pending = QuiescenceRules.IsLocalUpdatePending(htlc);

        // Assert
        Assert.Equal(expected, pending);
    }

    [Theory]
    [InlineData(HtlcState.SentAddHtlc, true)]
    [InlineData(HtlcState.SentAddCommit, true)]
    [InlineData(HtlcState.RcvdAddRevocation, true)]
    [InlineData(HtlcState.RcvdAddAckCommit, true)]
    [InlineData(HtlcState.SentAddAckRevocation, false)]
    [InlineData(HtlcState.RcvdAddHtlc, false)]
    [InlineData(HtlcState.RcvdAddCommit, false)]
    [InlineData(HtlcState.SentAddRevocation, false)]
    [InlineData(HtlcState.SentAddAckCommit, false)]
    [InlineData(HtlcState.RcvdAddAckRevocation, false)]
    public void Given_FeeUpdateState_When_IsLocalFeeUpdatePending_Then_OnlyOurUnfinalUpdateFeeCounts(
        HtlcState state, bool expected)
    {
        // Arrange: states 10-14 are the funder's own update_fee when we are the funder, 30-34 the peer's
        var feeUpdate = new FeeUpdate(1, 2_000, state);

        // Act
        var pending = QuiescenceRules.IsLocalFeeUpdatePending(feeUpdate);

        // Assert
        Assert.Equal(expected, pending);
    }

    [Fact]
    public void Given_APeerAddReceivedButNotRevoked_When_CheckSend_Then_OurStfuIsAllowedAndThePeersIsNot()
    {
        // Arrange: Bob adds and signs; Alice revokes but has not signed Bob's commitment with the add yet
        var pair = new CommitmentPair(500_000, 500_000);
        pair.BobAdd(10_000_000);
        pair.DeliverAliceRevoke(pair.BobCommits());

        // Act
        var alice = QuiescenceRules.CheckSend(true, true, s_requested, pair.Alice);
        var bob = QuiescenceRules.CheckSend(true, true, s_requested, pair.Bob);

        // Assert
        Assert.Equal(StfuSendBlocker.None, alice);
        Assert.Equal(StfuSendBlocker.LocalUpdatesPending, bob);
    }

    [Fact]
    public void Given_OurUnackedFeeUpdate_When_CheckSend_Then_BlockedUntilCommittedAndRevokedBothWays()
    {
        // Arrange: Alice (funder) sends update_fee and signs; Bob has not signed back yet
        var pair = new CommitmentPair(500_000, 500_000);
        pair.AliceFee(2_000);
        pair.DeliverBobRevoke(pair.AliceCommits());

        // Act
        var aliceBefore = QuiescenceRules.CheckSend(true, true, s_requested, pair.Alice);
        var bobBefore = QuiescenceRules.CheckSend(true, true, s_requested, pair.Bob);
        pair.DeliverAliceRevoke(pair.BobCommits());
        var aliceAfter = QuiescenceRules.CheckSend(true, true, s_requested, pair.Alice);

        // Assert
        Assert.Equal(StfuSendBlocker.LocalUpdatesPending, aliceBefore);
        Assert.Equal(StfuSendBlocker.None, bobBefore);
        Assert.Equal(StfuSendBlocker.None, aliceAfter);
    }

    [Fact]
    public void Given_OurFulfillNotYetFinal_When_CheckSend_Then_BlockedAndThePeerIsNot()
    {
        // Arrange: Alice's HTLC to Bob is locked in; Bob fulfills it (Bob's removal)
        var pair = new CommitmentPair(500_000, 500_000);
        var id = pair.AliceAdd(10_000_000);
        pair.Converge();
        pair.BobFulfill(id);

        // Act
        var bob = QuiescenceRules.CheckSend(true, true, s_requested, pair.Bob);
        var alice = QuiescenceRules.CheckSend(true, true, s_requested, pair.Alice);
        pair.Converge();
        var bobAfter = QuiescenceRules.CheckSend(true, true, s_requested, pair.Bob);

        // Assert
        Assert.Equal(StfuSendBlocker.LocalUpdatesPending, bob);
        Assert.Equal(StfuSendBlocker.None, alice);
        Assert.Equal(StfuSendBlocker.None, bobAfter);
    }

    [Fact]
    public void Given_ALockedInHtlcBothWays_When_CheckSend_Then_Allowed()
    {
        // Arrange: HTLCs may stay in the commitments: only pending updates block stfu
        var pair = new CommitmentPair(500_000, 500_000);
        pair.AliceAdd(10_000_000);
        pair.BobAdd(20_000_000);
        pair.Converge();

        // Act
        var alice = QuiescenceRules.CheckSend(true, true, s_requested, pair.Alice);
        var bob = QuiescenceRules.CheckSend(true, true, s_requested, pair.Bob);

        // Assert
        Assert.Equal(StfuSendBlocker.None, alice);
        Assert.Equal(StfuSendBlocker.None, bob);
        Assert.NotEmpty(pair.Alice.Htlcs);
    }

    [Fact]
    public void Given_NoEngineSnapshot_When_HasPendingLocalUpdates_Then_False()
    {
        // Act
        var pending = QuiescenceRules.HasPendingLocalUpdates(null);

        // Assert
        Assert.False(pending);
    }

    [Fact]
    public void Given_NothingOwed_When_CheckSend_Then_NothingOwed()
    {
        // Act
        var blocker = QuiescenceRules.CheckSend(true, true, QuiescenceState.None, null);

        // Assert
        Assert.Equal(StfuSendBlocker.NothingOwed, blocker);
    }

    [Fact]
    public void Given_LinkNotReady_When_CheckSend_Then_LinkNotReady()
    {
        // Act
        var blocker = QuiescenceRules.CheckSend(true, false, s_requested, null);

        // Assert
        Assert.Equal(StfuSendBlocker.LinkNotReady, blocker);
    }

    #endregion

    #region Q-S-03 "MUST NOT send stfu twice" / "if it is replying to an stfu: MUST set initiator to 0; otherwise: MUST set initiator to 1"

    [Fact]
    public void Given_OurStfuSent_When_CheckSend_Then_AlreadySent()
    {
        // Arrange
        var sent = QuiescenceRules.RecordSent(s_requested, true, s_now);

        // Act
        var blocker = QuiescenceRules.CheckSend(true, true, sent, null);

        // Assert
        Assert.Equal(StfuSendBlocker.AlreadySent, blocker);
    }

    [Fact]
    public void Given_OurStfuSent_When_RecordSentAgain_Then_Throws()
    {
        // Arrange
        var sent = QuiescenceRules.RecordSent(s_requested, true, s_now);

        // Act / Assert
        Assert.Throws<InvalidOperationException>(() => QuiescenceRules.RecordSent(sent, true, s_now));
    }

    [Fact]
    public void Given_OurOwnRequest_When_RecordSent_Then_InitiatorIsOne()
    {
        // Act
        var flag = QuiescenceRules.InitiatorFlagToSend(s_requested);
        var sent = QuiescenceRules.RecordSent(s_requested, false, s_now);

        // Assert
        Assert.True(flag);
        Assert.True(sent.SentStfuInitiator);
    }

    [Fact]
    public void Given_ThePeersStfu_When_WeReply_Then_InitiatorIsZero()
    {
        // Arrange
        var received = QuiescenceRules.Receive(QuiescenceState.None, true, true, false, s_now).Next;

        // Act
        var flag = QuiescenceRules.InitiatorFlagToSend(received);
        var replied = QuiescenceRules.RecordSent(received, false, s_now);

        // Assert
        Assert.False(flag);
        Assert.False(replied.SentStfuInitiator);
    }

    [Fact]
    public void Given_ThePeersStfuReceived_When_ASecondStfuArrives_Then_ViolationAndStateUnchanged()
    {
        // Arrange
        var received = QuiescenceRules.Receive(QuiescenceState.None, true, true, false, s_now).Next;

        // Act
        var result = QuiescenceRules.Receive(received, true, true, false, s_now);

        // Assert
        Assert.Equal(QuiescenceViolation.SecondStfu, result.Violation);
    }

    [Fact]
    public void Given_QuiescentChannel_When_AnotherStfuArrives_Then_SecondStfu()
    {
        // Arrange
        var sent = QuiescenceRules.RecordSent(s_requested, true, s_now);
        var quiescent = QuiescenceRules.Receive(sent, false, true, true, s_now).Next;

        // Act
        var result = QuiescenceRules.Receive(quiescent, false, true, true, s_now);

        // Assert
        Assert.Equal(QuiescenceViolation.SecondStfu, result.Violation);
    }

    [Fact]
    public void Given_WeSentNoStfu_When_AReplyWithInitiatorZeroArrives_Then_UnsolicitedReply()
    {
        // Act
        var result = QuiescenceRules.Receive(QuiescenceState.None, false, true, true, s_now);

        // Assert
        Assert.Equal(QuiescenceViolation.UnsolicitedReply, result.Violation);
    }

    #endregion

    #region Q-S-04 "MUST now consider the channel to be quiescing" / "MUST NOT send an update message after stfu"

    [Fact]
    public void Given_OurStfuSent_When_Checked_Then_QuiescingAndNoUpdateMayBeProposed()
    {
        // Act
        var sent = QuiescenceRules.RecordSent(s_requested, true, s_now);

        // Assert
        Assert.True(sent.IsQuiescing);
        Assert.False(sent.IsQuiescent);
        Assert.True(sent.BlocksNewLocalUpdates);
        Assert.False(QuiescenceRules.MayProposeUpdate(sent));
    }

    [Fact]
    public void Given_NoQuiescence_When_MayProposeUpdate_Then_True()
    {
        // Act / Assert
        Assert.True(QuiescenceRules.MayProposeUpdate(QuiescenceState.None));
    }

    [Theory]
    [InlineData(MessageTypes.UpdateAddHtlc, true)]
    [InlineData(MessageTypes.UpdateFulfillHtlc, true)]
    [InlineData(MessageTypes.UpdateFailHtlc, true)]
    [InlineData(MessageTypes.UpdateFailMalformedHtlc, true)]
    [InlineData(MessageTypes.UpdateFee, true)]
    [InlineData(MessageTypes.CommitmentSigned, false)]
    [InlineData(MessageTypes.RevokeAndAck, false)]
    [InlineData(MessageTypes.Shutdown, false)]
    [InlineData(MessageTypes.ChannelReestablish, false)]
    public void Given_ThePeersStfuReceived_When_CheckPeerMessage_Then_OnlyUpdatesViolate(MessageTypes type,
                                                                                        bool violates)
    {
        // Arrange: commitment_signed and revoke_and_ack still flow, so pending changes get committed and revoked
        var received = QuiescenceRules.Receive(QuiescenceState.None, true, true, false, s_now).Next;

        // Act
        var violation = QuiescenceRules.CheckPeerMessage(received, type);

        // Assert
        Assert.Equal(violates ? QuiescenceViolation.UpdateAfterStfu : null, violation);
    }

    [Theory]
    [InlineData(MessageTypes.UpdateAddHtlc)]
    [InlineData(MessageTypes.UpdateFee)]
    public void Given_OnlyOurStfuSent_When_ThePeerSendsAnUpdate_Then_NoViolation(MessageTypes type)
    {
        // Arrange: the peer may still send updates until it has sent its own stfu (it SHOULD NOT, Q-R-02; it is not a
        // MUST NOT, and an update crossing our stfu is normal)
        var sent = QuiescenceRules.RecordSent(s_requested, true, s_now);

        // Act
        var violation = QuiescenceRules.CheckPeerMessage(sent, type);

        // Assert
        Assert.Null(violation);
    }

    [Theory]
    [InlineData(QuiescenceViolation.NotNegotiated, "Q-S-01")]
    [InlineData(QuiescenceViolation.SecondStfu, "Q-S-03")]
    [InlineData(QuiescenceViolation.UnsolicitedReply, "Q-S-03")]
    [InlineData(QuiescenceViolation.UpdateAfterStfu, "Q-S-04")]
    public void Given_AViolation_When_CreateWarning_Then_ChannelWarningThatClosesTheConnection(
        QuiescenceViolation violation, string requirement)
    {
        // Act
        var warning = QuiescenceRules.CreateWarning(violation, s_channelId);

        // Assert
        Assert.True(warning.CloseConnection);
        Assert.Equal(s_channelId, warning.ChannelId);
        Assert.Contains(requirement, warning.Message);
        Assert.False(string.IsNullOrWhiteSpace(warning.PeerMessage));
    }

    #endregion

    #region Q-R-01 "The receiver of stfu: if it has sent stfu then: MUST now consider the channel to be quiescent"

    [Fact]
    public void Given_OurStfuSent_When_ThePeersReplyArrives_Then_QuiescentWithUsAsInitiator()
    {
        // Arrange
        var sent = QuiescenceRules.RecordSent(s_requested, false, s_now.AddSeconds(-1));

        // Act
        var result = QuiescenceRules.Receive(sent, false, true, false, s_now);

        // Assert
        Assert.Null(result.Violation);
        Assert.True(result.Next.IsQuiescent);
        Assert.False(result.Next.IsQuiescing);
        Assert.Equal(QuiescenceInitiator.Local, result.Next.Initiator);
        Assert.Equal(s_now, result.Next.QuiescentSince);
        Assert.Equal(QuiescencePurpose.Splice, result.Next.PendingRequest);
        Assert.True(result.Next.BlocksNewLocalUpdates);
    }

    #endregion

    #region Q-R-02 "otherwise: SHOULD NOT send any more update messages; MUST reply with stfu once it can do so"

    [Fact]
    public void Given_NoStfuSent_When_ThePeersStfuArrives_Then_UpdatesStopAndOurReplyIsOwed()
    {
        // Act
        var result = QuiescenceRules.Receive(QuiescenceState.None, true, true, false, s_now);

        // Assert
        Assert.Null(result.Violation);
        Assert.True(result.Next.IsQuiescing);
        Assert.False(result.Next.IsQuiescent);
        Assert.Null(result.Next.Initiator);
        Assert.False(QuiescenceRules.MayProposeUpdate(result.Next));
        Assert.True(QuiescenceRules.IsStfuOwed(result.Next));
        Assert.False(QuiescenceRules.InitiatorFlagToSend(result.Next));
    }

    [Fact]
    public void Given_ThePeersStfuAndOurPendingAdd_When_TheAddIsCommittedAndRevoked_Then_TheReplyIsReleased()
    {
        // Arrange: Alice has an unsigned add when Bob's stfu arrives; she must first get it committed both ways
        var pair = new CommitmentPair(500_000, 500_000);
        pair.AliceAdd(10_000_000);
        var received = QuiescenceRules.Receive(QuiescenceState.None, true, true, true, s_now).Next;

        // Act
        var before = QuiescenceRules.CheckSend(true, true, received, pair.Alice);
        pair.DeliverBobRevoke(pair.AliceCommits());
        var afterOneWay = QuiescenceRules.CheckSend(true, true, received, pair.Alice);
        pair.DeliverAliceRevoke(pair.BobCommits());
        var afterBothWays = QuiescenceRules.CheckSend(true, true, received, pair.Alice);
        var replied = QuiescenceRules.RecordSent(received, true, s_now);

        // Assert
        Assert.Equal(StfuSendBlocker.LocalUpdatesPending, before);
        Assert.Equal(StfuSendBlocker.LocalUpdatesPending, afterOneWay);
        Assert.Equal(StfuSendBlocker.None, afterBothWays);
        Assert.False(replied.SentStfuInitiator);
        Assert.True(replied.IsQuiescent);
        Assert.Equal(QuiescenceInitiator.Remote, replied.Initiator);
        Assert.Equal(s_now, replied.QuiescentSince);
    }

    #endregion

    #region Q-R-05 "If both sides send stfu simultaneously ... the 'initiator' is arbitrarily considered to be the channel funder (the sender of open_channel)"

    [Theory]
    [InlineData(true, false, true, QuiescenceInitiator.Local)]
    [InlineData(true, false, false, QuiescenceInitiator.Local)]
    [InlineData(false, true, true, QuiescenceInitiator.Remote)]
    [InlineData(false, true, false, QuiescenceInitiator.Remote)]
    [InlineData(true, true, true, QuiescenceInitiator.Local)]
    [InlineData(true, true, false, QuiescenceInitiator.Remote)]
    public void Given_BothInitiatorFlags_When_ResolveInitiator_Then_TheSenderOfOneOrTheFunderWins(
        bool sent, bool received, bool localIsFunder, QuiescenceInitiator expected)
    {
        // Act
        var initiator = QuiescenceRules.ResolveInitiator(sent, received, localIsFunder);

        // Assert
        Assert.Equal(expected, initiator);
    }

    [Fact]
    public void Given_NeitherFlagSet_When_ResolveInitiator_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => QuiescenceRules.ResolveInitiator(false, false, true));
    }

    [Theory]
    [InlineData(true, QuiescenceInitiator.Local)]
    [InlineData(false, QuiescenceInitiator.Remote)]
    public void Given_SimultaneousStfuWithInitiatorOne_When_Received_Then_TheFunderIsTheInitiator(
        bool localIsFunder, QuiescenceInitiator expected)
    {
        // Arrange: we sent stfu(1) for our request before the peer's stfu(1) arrived
        var sent = QuiescenceRules.RecordSent(s_requested, localIsFunder, s_now);

        // Act
        var result = QuiescenceRules.Receive(sent, true, true, localIsFunder, s_now);

        // Assert
        Assert.Null(result.Violation);
        Assert.True(result.Next.IsQuiescent);
        Assert.Equal(expected, result.Next.Initiator);
    }

    #endregion
}

/// <summary>
/// The <see cref="StfuReceiveResult"/> union read as the record it was before the C# 15 pilot (NL-1086): the next state
/// (the test fails on a violation) or the violation (null for a state).
/// </summary>
file static class StfuReceiveResultView
{
    extension(StfuReceiveResult result)
    {
        public QuiescenceState Next => Assert.IsType<QuiescenceState>(result.Value);

        public QuiescenceViolation? Violation => result.Value as QuiescenceViolation?;
    }
}