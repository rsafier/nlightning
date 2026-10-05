using NLightning.Tests.Utils.Channels;

namespace NLightning.Domain.Tests.Channels.Reestablish;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Reestablish;

/// <summary>
/// Splicing plan SP2-A-T1: the splice and interactive-funding rules of <see cref="ReestablishPlanner"/> (SP-RE-01..06,
/// BOLT 2 "Message Retransmission" with <c>next_funding</c> and <c>my_current_funding_locked</c>), as tables.
/// </summary>
public class ReestablishPlannerSpliceTests
{
    private static readonly TxId s_funding = Tx(0x10);
    private static readonly TxId s_splice = Tx(0x20);
    private static readonly TxId s_other = Tx(0x30);

    #region SP-RE-01 our next_funding

    public static TheoryData<bool, bool, bool, bool, bool, byte?> OwnNextFundingCases => new()
    {
        // CS sent, CS received, tx_sigs sent, tx_sigs received, splice -> flags (null = no TLV)
        { false, false, false, false, true, null }, // nothing signed: not remembered
        { true, false, false, false, true, 1 }, // theirs missing: ask for it
        { true, true, false, false, true, 0 },
        { true, true, true, false, true, 0 }, // our tx_signatures sent, theirs missing
        { true, true, false, true, true, null }, // theirs received: done for us
        { true, true, true, true, true, null },
        { true, false, false, false, false, 1 }, // a dual-funded open follows the same rule
        { true, true, true, false, false, 0 }
    };

    [Theory]
    [MemberData(nameof(OwnNextFundingCases))]
    public void Given_TheLatestInteractiveTx_When_CreatingOwn_Then_NextFundingFollowsSpRe01(
        bool csSent, bool csReceived, bool sigsSent, bool sigsReceived, bool isSplice, byte? expectedFlags)
    {
        // Arrange
        var local = State(latest: new ReestablishInteractiveTxState(s_splice, isSplice, csSent, csReceived, sigsSent,
                                                                     sigsReceived, false));

        // Act
        var own = ReestablishPlanner.CreateOwn(local);

        // Assert
        if (expectedFlags is null)
        {
            Assert.Null(own.NextFunding);
            return;
        }

        Assert.NotNull(own.NextFunding);
        Assert.Equal(s_splice, own.NextFunding.TxId);
        Assert.Equal(expectedFlags.Value, own.NextFunding.RetransmitFlags);
        Assert.Equal(11UL, own.NextCommitmentNumber); // never lowered (retransmit_flags replace that convention)
    }

    [Fact]
    public void Given_NoInteractiveTx_When_CreatingOwn_Then_NoTlv()
    {
        // Act
        var own = ReestablishPlanner.CreateOwn(State());

        // Assert
        Assert.Null(own.NextFunding);
        Assert.Null(own.MyCurrentFundingLocked);
    }

    #endregion

    #region SP-RE-02 our my_current_funding_locked

    public static TheoryData<string, bool, bool, bool, bool, string?, byte> OwnFundingLockedCases => new()
    {
        // case, channel_ready sent, splice_locked sent for the pending splice, current is a splice, public,
        // expected txid ("funding"/"splice"/null), expected flags
        { "never ready", false, false, false, false, null, 0 },
        { "ready, private", true, false, false, false, "funding", 0 },
        { "ready, public, no sigs", true, false, false, true, "funding", 1 },
        { "splice locked sent while pending", true, true, false, false, "splice", 0 },
        { "splice locked sent, public", true, true, false, true, "splice", 1 },
        { "current is a locked splice", true, false, true, false, "splice", 0 }
    };

    [Theory]
    [MemberData(nameof(OwnFundingLockedCases))]
    public void Given_TheFundingLockFacts_When_CreatingOwn_Then_MyCurrentFundingLockedFollowsSpRe02(
        string name, bool channelReady, bool spliceLockedSent, bool currentIsSplice, bool isPublic,
        string? expected, byte expectedFlags)
    {
        // Arrange
        var current = currentIsSplice ? s_splice : s_funding;
        List<ReestablishPendingSplice> pending = currentIsSplice
                                                     ? []
                                                     : [new ReestablishPendingSplice(s_splice, spliceLockedSent, false)];
        var lastSent = spliceLockedSent || currentIsSplice ? s_splice : (TxId?)null;
        var local = State(splice: new ReestablishSpliceState(channelReady, current, lastSent, isPublic, pending, [], [],
                                                             currentIsSplice));

        // Act
        var own = ReestablishPlanner.CreateOwn(local);

        // Assert
        if (expected is null)
        {
            Assert.Null(own.MyCurrentFundingLocked);
            return;
        }

        Assert.NotNull(own.MyCurrentFundingLocked);
        Assert.True(own.MyCurrentFundingLocked.TxId == (expected == "splice" ? s_splice : s_funding), name);
        Assert.Equal(expectedFlags, own.MyCurrentFundingLocked.RetransmitFlags);
    }

    [Fact]
    public void Given_ThePeersAnnouncementSignaturesHeld_When_CreatingOwn_Then_Bit0IsClear()
    {
        // Arrange (SP-RE-02: bit 0 only when we did not receive announcement_signatures for that txid)
        var local = State(splice: new ReestablishSpliceState(true, s_funding, null, true, [], [s_funding], []));

        // Act
        var own = ReestablishPlanner.CreateOwn(local);

        // Assert
        Assert.Equal(0, own.MyCurrentFundingLocked!.RetransmitFlags);
    }

    [Fact]
    public void Given_NoSpliceNegotiated_When_CreatingOwn_Then_NoMyCurrentFundingLocked()
    {
        // Act (Splice null: option_splice not negotiated)
        var own = ReestablishPlanner.CreateOwn(State());

        // Assert
        Assert.Null(own.MyCurrentFundingLocked);
    }

    #endregion

    #region SP-RE-03 the peer's next_funding

    public static TheoryData<string, bool, bool, bool, bool, bool, byte, ReestablishStep[]> PeerNextFundingCases =>
        new()
        {
            // case, CS sent, CS received, tx_sigs sent, tx_sigs received, we sign first, peer's flags -> steps
            { "SP-T-04 both CS lost", true, false, false, false, false, 1, [ReestablishStep.NextFundingCommitmentSigned] },
            { "SP-T-04 both CS lost, we sign first", true, false, false, false, true, 1,
              [ReestablishStep.NextFundingCommitmentSigned] },
            { "SP-T-05 our tx_sigs lost (we sign first)", true, true, true, false, true, 0,
              [ReestablishStep.NextFundingTxSignatures] },
            { "SP-T-05 theirs lost (they sign first)", true, true, false, false, false, 0, [] },
            { "SP-T-06 theirs received, ours lost", true, true, true, true, false, 0,
              [ReestablishStep.NextFundingTxSignatures] },
            { "peer lacks our CS, we have theirs", true, true, true, false, true, 1,
              [ReestablishStep.NextFundingCommitmentSigned, ReestablishStep.NextFundingTxSignatures] }
        };

    [Theory]
    [MemberData(nameof(PeerNextFundingCases))]
    public void Given_ThePeersNextFundingNamingOurLatest_When_Planning_Then_TheSigningStepsAreRetransmitted(
        string name, bool csSent, bool csReceived, bool sigsSent, bool sigsReceived, bool signsFirst, byte flags,
        ReestablishStep[] expected)
    {
        // Arrange
        var local = State(latest: new ReestablishInteractiveTxState(s_splice, true, csSent, csReceived, sigsSent,
                                                                     sigsReceived, signsFirst));

        // Act
        var plan = Plan(local, new ReestablishFundingField(s_splice, flags));

        // Assert
        Assert.True(plan.Outcome == ReestablishOutcome.Resume, $"{name}: {plan.Reason}");
        Assert.Equal(expected, plan.Steps);
    }

    [Fact]
    public void Given_AnUnknownNextFunding_When_Planning_Then_TxAbort()
    {
        // Arrange (SP-T-03: the peer remembers a splice we never finished constructing)
        var local = State(splice: Splice());

        // Act
        var plan = Plan(local, new ReestablishFundingField(s_splice, 1));

        // Assert
        Assert.Equal(ReestablishOutcome.Resume, plan.Outcome);
        Assert.Equal([ReestablishStep.TxAbort], plan.Steps);
    }

    [Fact]
    public void Given_BothSetNextFundingWithDifferentTxIds_When_Planning_Then_TheChannelFailsWithoutBroadcast()
    {
        // Arrange
        var local = State(latest: new ReestablishInteractiveTxState(s_splice, true, true, true, false, false, false));

        // Act
        var plan = Plan(local, new ReestablishFundingField(s_other, 0));

        // Assert
        Assert.Equal(ReestablishOutcome.Fail, plan.Outcome);
        Assert.Equal("SP-RE-03", plan.RequirementId);
        Assert.False(plan.MustBroadcast);
    }

    [Fact]
    public void Given_ANextFundingForAnOlderTxWhileOursIsNotSet_When_Planning_Then_TxAbort()
    {
        // Arrange: our latest is fully signed (no next_funding of ours), the peer names another one
        var local = State(latest: new ReestablishInteractiveTxState(s_splice, true, true, true, true, true, false));

        // Act
        var plan = Plan(local, new ReestablishFundingField(s_other, 0));

        // Assert
        Assert.Equal([ReestablishStep.TxAbort], plan.Steps);
    }

    [Fact]
    public void Given_TheOlderConventionNextCommitmentNumber_When_Planning_Then_ItAsksForOurCommitmentSigned()
    {
        // Arrange (bolt02/splicing-test.md SP-T-04: next_commitment_number = the current number, no flag)
        var local = State(latest: new ReestablishInteractiveTxState(s_splice, true, true, false, false, false, false));

        // Act
        var plan = ReestablishPlanner.Plan(local,
                                           new PeerReestablish(10, 10, Secret(9), true,
                                                               new ReestablishFundingField(s_splice)), IsSecret);

        // Assert
        Assert.Equal(ReestablishOutcome.Resume, plan.Outcome);
        Assert.Equal([ReestablishStep.NextFundingCommitmentSigned], plan.Steps);
    }

    [Fact]
    public void Given_TheOlderConventionWithoutNextFunding_When_Planning_Then_ItStillFails()
    {
        // Act (B2-RE-19 unchanged without an interactive transaction to finish, SP-RE-06)
        var plan = ReestablishPlanner.Plan(State(), new PeerReestablish(10, 10, Secret(9)), IsSecret);

        // Assert
        Assert.Equal(ReestablishOutcome.Fail, plan.Outcome);
        Assert.Equal("B2-RE-19", plan.RequirementId);
    }

    [Fact]
    public void Given_NextFundingSteps_When_ACommitDiffIsAlsoDue_Then_TheSigningStepsGoFirst()
    {
        // Arrange (SP-T-07: tx_signatures before the retransmitted updates and batch)
        var local = new ReestablishLocalState(10, 10, true, true, LastSentCommitmentMessage.CommitmentSigned, false,
                                              new ReestablishInteractiveTxState(s_splice, true, true, true, true, true,
                                                                                true));

        // Act
        var plan = Plan(local, new ReestablishFundingField(s_splice, 0));

        // Assert
        Assert.Equal([ReestablishStep.NextFundingTxSignatures, ReestablishStep.CommitDiff], plan.Steps);
    }

    #endregion

    #region SP-RE-04 the peer's my_current_funding_locked

    [Fact]
    public void Given_ThePeersFundingLockedNamesAPendingSplice_When_Planning_Then_ItIsItsSpliceLocked()
    {
        // Arrange (SP-T-08)
        var local = State(splice: Splice(pending: [new ReestablishPendingSplice(s_splice, true, false)]));

        // Act
        var plan = Plan(local, fundingLocked: new ReestablishFundingField(s_splice));

        // Assert
        Assert.Equal(s_splice, plan.PeerSpliceLocked);
        Assert.Empty(plan.Steps);
    }

    public static TheoryData<string, bool, string> NoSpliceLockedCases => new()
    {
        { "already received", true, "splice" },
        { "the current funding", false, "funding" },
        { "an unknown txid", false, "other" }
    };

    [Theory]
    [MemberData(nameof(NoSpliceLockedCases))]
    public void Given_AFundingLockedThatIsNoNewSpliceLocked_When_Planning_Then_NothingIsLocked(
        string name, bool received, string txName)
    {
        // Arrange
        var local = State(splice: Splice(pending: [new ReestablishPendingSplice(s_splice, false, received)]));
        var txId = txName switch
        {
            "splice" => s_splice,
            "funding" => s_funding,
            _ => s_other
        };

        // Act
        var plan = Plan(local, fundingLocked: new ReestablishFundingField(txId));

        // Assert
        Assert.True(plan.PeerSpliceLocked is null, name);
    }

    [Fact]
    public void Given_NoSpliceNegotiated_When_ThePeerSendsFundingLocked_Then_ItIsIgnored()
    {
        // Act
        var plan = Plan(State(), fundingLocked: new ReestablishFundingField(s_splice, 1));

        // Assert
        Assert.Null(plan.PeerSpliceLocked);
        Assert.Empty(plan.Steps);
    }

    public static TheoryData<string, bool, bool, bool> AnnouncementCases => new()
    {
        // case, bit 0, public, ready for that funding -> retransmit
        { "asked, public, ready", true, true, true },
        { "not asked", false, true, true },
        { "private", true, false, true },
        { "not ready", true, true, false }
    };

    [Theory]
    [MemberData(nameof(AnnouncementCases))]
    public void Given_Bit0OfFundingLocked_When_Planning_Then_OurAnnouncementSignaturesFollowSpRe04(
        string name, bool bit0, bool isPublic, bool ready)
    {
        // Arrange
        var local = State(splice: new ReestablishSpliceState(true, s_funding, null, isPublic, [], [],
                                                             ready ? [s_funding] : []));

        // Act
        var plan = Plan(local, fundingLocked: new ReestablishFundingField(s_funding, bit0 ? (byte)1 : (byte)0));

        // Assert
        Assert.True(plan.Steps.Contains(ReestablishStep.AnnouncementSignatures) == (bit0 && isPublic && ready), name);
    }

    [Fact]
    public void Given_Bit0ForASpliceItLocksNow_When_Planning_Then_AnnouncementSignaturesComeLast()
    {
        // Arrange: the peer's funding locked completes our lock; readiness is checked again when the step runs
        var local = State(splice: Splice(isPublic: true, pending: [new ReestablishPendingSplice(s_splice, true, false)]));

        // Act
        var plan = Plan(local, fundingLocked: new ReestablishFundingField(s_splice, 1));

        // Assert
        Assert.Equal(s_splice, plan.PeerSpliceLocked);
        Assert.Equal([ReestablishStep.AnnouncementSignatures], plan.Steps);
    }

    #endregion

    #region SP-RE-05 channel_ready

    public static TheoryData<string, bool> ChannelReadyCases => new()
    {
        // case -> channel_ready retransmitted
        { "no TLV", true },
        { "both name the original funding", true },
        { "our next_funding for a splice", false },
        { "the peer's next_funding for a splice", false },
        { "our funding locked names a splice", false },
        { "the peer's funding locked names a splice", false },
        { "a dual-funded open's next_funding", true }
    };

    [Theory]
    [MemberData(nameof(ChannelReadyCases))]
    public void Given_BothNextCommitmentNumbersOne_When_Planning_Then_ChannelReadyFollowsSpRe05(string name,
        bool expected)
    {
        // Arrange
        ReestablishInteractiveTxState? latest = null;
        ReestablishFundingField? peerNextFunding = null;
        ReestablishFundingField? peerLocked = null;
        var splice = new ReestablishSpliceState(true, s_funding, null, false,
                                                [new ReestablishPendingSplice(s_splice, false, false)], [], []);
        switch (name)
        {
            case "no TLV":
                splice = null!;
                break;
            case "both name the original funding":
                peerLocked = new ReestablishFundingField(s_funding);
                break;
            case "our next_funding for a splice":
                latest = new ReestablishInteractiveTxState(s_splice, true, true, true, false, false, false);
                break;
            case "the peer's next_funding for a splice":
                peerNextFunding = new ReestablishFundingField(s_other);
                break;
            case "our funding locked names a splice":
                splice = splice with
                {
                    LastSpliceLockedSent = s_splice,
                    PendingSplices = [new ReestablishPendingSplice(s_splice, true, false)]
                };
                break;
            case "the peer's funding locked names a splice":
                peerLocked = new ReestablishFundingField(s_splice);
                break;
            case "a dual-funded open's next_funding":
                splice = null!;
                latest = new ReestablishInteractiveTxState(s_splice, false, true, true, true, true, false);
                peerNextFunding = new ReestablishFundingField(s_splice);
                break;
        }

        var local = new ReestablishLocalState(0, 0, false, false, LastSentCommitmentMessage.None, false, latest,
                                              splice);

        // Act
        var plan = ReestablishPlanner.Plan(local,
                                           new PeerReestablish(1, 0, new byte[32], peerNextFunding is not null,
                                                               peerNextFunding, peerLocked), IsSecret);

        // Assert
        Assert.True(plan.Outcome == ReestablishOutcome.Resume, $"{name}: {plan.Reason}");
        Assert.True(plan.Steps.Contains(ReestablishStep.ChannelReady) == expected, name);
    }

    #endregion

    #region SP-RE-06 the number rules are unchanged

    [Fact]
    public void Given_ASpliceAndALostRevokeAndAck_When_Planning_Then_TheNumberRulesStillApply()
    {
        // Arrange (SP-T-10 on Bob's side: his revoke_and_ack and batch were lost, both over a pending splice)
        var local = new ReestablishLocalState(11, 10, true, true, LastSentCommitmentMessage.CommitmentSigned, false,
                                              null, Splice(pending: [new ReestablishPendingSplice(s_splice, false, false)]));

        // Act: Alice expects commitment 11 and revocation 10
        var plan = ReestablishPlanner.Plan(local, new PeerReestablish(11, 10, Secret(9)), IsSecret);

        // Assert: the revoke_and_ack, then the stored batch, in their original order
        Assert.Equal(ReestablishOutcome.Resume, plan.Outcome);
        Assert.Equal([ReestablishStep.RevokeAndAck, ReestablishStep.CommitDiff], plan.Steps);
    }

    #endregion

    private static ReestablishSpliceState Splice(bool isPublic = false, ReestablishPendingSplice[]? pending = null)
    {
        pending ??= [];
        return new ReestablishSpliceState(true, s_funding, pending.LastOrDefault(p => p.SpliceLockedSent)?.TxId,
                                          isPublic, pending, [], []);
    }

    /// <summary>A channel at commitment 10 both ways with nothing to retransmit.</summary>
    private static ReestablishLocalState State(ReestablishInteractiveTxState? latest = null,
                                               ReestablishSpliceState? splice = null) =>
        new(10, 10, false, false, LastSentCommitmentMessage.RevokeAndAck, false, latest, splice);

    /// <summary>The peer's matching channel_reestablish (11/10) with the given funding TLVs.</summary>
    private static ReestablishPlan Plan(ReestablishLocalState local, ReestablishFundingField? nextFunding = null,
                                        ReestablishFundingField? fundingLocked = null) =>
        ReestablishPlanner.Plan(local,
                                new PeerReestablish(11, 10, Secret(9), nextFunding is not null, nextFunding,
                                                    fundingLocked), IsSecret);

    private static TxId Tx(byte tag) => new(Enumerable.Repeat(tag, 32).ToArray());

    private static byte[] Secret(ulong number)
    {
        var bytes = new byte[32];
        BitConverter.GetBytes(number + 1).CopyTo(bytes, 0);
        bytes[31] = 0x5E;
        return bytes;
    }

    private static bool IsSecret(ulong number, ReadOnlyMemory<byte> secret) =>
        secret.Span.SequenceEqual(Secret(number));
}