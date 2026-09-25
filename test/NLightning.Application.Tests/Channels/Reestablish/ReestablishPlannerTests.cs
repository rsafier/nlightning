namespace NLightning.Application.Tests.Channels.Reestablish;

using Domain.Channels.Enums;
using Domain.Channels.Reestablish;
using Domain.Protocol.Models;

/// <summary>
/// BOLT2 plan N7-T1: the pure <see cref="ReestablishPlanner"/> against the BOLT 2 "Message Retransmission" rules, as
/// named cases (plan §6.9 ids) and as an exhaustive table checked against a spec-literal oracle.
/// </summary>
public class ReestablishPlannerTests
{
    private static readonly byte[] s_zeroes = new byte[32];

    #region Our channel_reestablish

    [Fact]
    public void Given_Numbers_When_CreatingOwn_Then_NextCommitmentIsLPlusOneAndRevocationIsR()
    {
        // Arrange (B2-RE-08..11)
        var local = State(l: 5, r: 3);

        // Act
        var own = ReestablishPlanner.CreateOwn(local);

        // Assert
        Assert.Equal(6UL, own.NextCommitmentNumber);
        Assert.Equal(3UL, own.NextRevocationNumber);
        Assert.Equal(2UL, own.LastReceivedSecretNumber);
        Assert.Equal(5UL, own.CurrentPointNumber);
    }

    [Fact]
    public void Given_NoRevocationReceived_When_CreatingOwn_Then_SecretIsZeroes()
    {
        // Act (B2-RE-11: next_revocation_number 0 -> all-zero secret)
        var own = ReestablishPlanner.CreateOwn(State(l: 0, r: 0));

        // Assert
        Assert.Equal(1UL, own.NextCommitmentNumber);
        Assert.Equal(0UL, own.NextRevocationNumber);
        Assert.Null(own.LastReceivedSecretNumber);
        Assert.Equal(0UL, own.CurrentPointNumber);
    }

    #endregion

    #region Named cases

    [Fact]
    public void Given_BothNextCommitmentNumbersOne_When_Planning_Then_ChannelReadyIsResent()
    {
        // Act (B2-RE-15)
        var plan = Plan(State(l: 0, r: 0), x: 1, y: 0, s_zeroes);

        // Assert
        Assert.Equal(ReestablishOutcome.Resume, plan.Outcome);
        Assert.Equal([ReestablishStep.ChannelReady], plan.Steps);
    }

    [Fact]
    public void Given_NormalOperationStarted_When_Planning_Then_NoChannelReady()
    {
        // Act (B2-RE-16)
        var plan = Plan(State(l: 2, r: 2), x: 3, y: 2, Secret(1));

        // Assert
        Assert.Equal(ReestablishOutcome.Resume, plan.Outcome);
        Assert.Empty(plan.Steps);
    }

    [Fact]
    public void Given_NextCommitmentNumberZero_When_Planning_Then_FailAndBroadcast()
    {
        // Act (B2-RE-14)
        var plan = Plan(State(l: 1, r: 1), x: 0, y: 1, Secret(0));

        // Assert
        Assert.Equal(ReestablishOutcome.Fail, plan.Outcome);
        Assert.Equal("B2-RE-14", plan.RequirementId);
        Assert.True(plan.MustBroadcast);
    }

    [Fact]
    public void Given_LastCommitmentSignedLost_When_Planning_Then_TheStoredDiffIsResent()
    {
        // Arrange (B2-RE-18): we signed the peer's commitment 3, it still expects 3
        var local = State(l: 2, r: 2, hasNext: true);

        // Act
        var plan = Plan(local, x: 3, y: 2, Secret(1));

        // Assert
        Assert.Equal([ReestablishStep.CommitDiff], plan.Steps);
    }

    [Fact]
    public void Given_LastCommitmentSignedReceived_When_Planning_Then_NothingIsResent()
    {
        // Arrange: the peer has our commitment 3 and will re-send its revoke_and_ack
        var local = State(l: 2, r: 2, hasNext: true);

        // Act
        var plan = Plan(local, x: 4, y: 2, Secret(1));

        // Assert
        Assert.Equal(ReestablishOutcome.Resume, plan.Outcome);
        Assert.Empty(plan.Steps);
    }

    [Fact]
    public void Given_DiffNotStored_When_ItMustBeResent_Then_Fail()
    {
        // Act
        var plan = Plan(State(l: 2, r: 2, hasNext: true, hasDiff: false), x: 3, y: 2, Secret(1));

        // Assert
        Assert.Equal(ReestablishOutcome.Fail, plan.Outcome);
        Assert.Equal("B2-RE-18", plan.RequirementId);
    }

    [Theory]
    [InlineData(false, 5UL)]
    [InlineData(false, 2UL)]
    [InlineData(true, 5UL)]
    [InlineData(true, 2UL)]
    public void Given_UnexpectedNextCommitmentNumber_When_Planning_Then_Fail(bool hasNext, ulong x)
    {
        // Act (B2-RE-19)
        var plan = Plan(State(l: 2, r: 2, hasNext: hasNext), x, y: 2, Secret(1));

        // Assert
        Assert.Equal(ReestablishOutcome.Fail, plan.Outcome);
        Assert.Equal("B2-RE-19", plan.RequirementId);
    }

    [Theory]
    [InlineData(LastSentCommitmentMessage.CommitmentSigned, new[] { ReestablishStep.RevokeAndAck, ReestablishStep.CommitDiff })]
    [InlineData(LastSentCommitmentMessage.RevokeAndAck, new[] { ReestablishStep.CommitDiff, ReestablishStep.RevokeAndAck })]
    public void Given_RevokeAndAckAndCommitmentSignedLost_When_Planning_Then_TheirOriginalOrderIsKept(
        LastSentCommitmentMessage lastSent, ReestablishStep[] expected)
    {
        // Arrange (B2-RE-20): the peer misses our revoke_and_ack of 1 and our commitment_signed 3
        var local = State(l: 2, r: 2, hasNext: true, lastSent: lastSent);

        // Act
        var plan = Plan(local, x: 3, y: 1, Secret(0));

        // Assert
        Assert.Equal(expected, plan.Steps);
    }

    [Fact]
    public void Given_RevokeAndAckLost_When_Planning_Then_ItIsResent()
    {
        // Act: the peer only holds our secret 0 (secret 1 was in the lost revoke_and_ack)
        var plan = Plan(State(l: 2, r: 2), x: 3, y: 1, Secret(0));

        // Assert
        Assert.Equal([ReestablishStep.RevokeAndAck], plan.Steps);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(4UL)]
    public void Given_RevocationNumberTooOld_When_Planning_Then_Fail(ulong y)
    {
        // Arrange (B2-RE-21): 0 is two behind our commitment 2; 4 ahead without proof
        var plan = Plan(State(l: 2, r: 2), x: 3, y, y == 0 ? s_zeroes : Secret(99));

        // Assert
        Assert.Equal(ReestablishOutcome.Fail, plan.Outcome);
        Assert.Equal("B2-RE-21", plan.RequirementId);
    }

    [Fact]
    public void Given_PeerAheadWithOurSecret_When_Planning_Then_DataLoss()
    {
        // Arrange (B2-RE-23): we are at commitment 2 but the peer expects revocation 5 and knows our secret 4
        var plan = Plan(State(l: 2, r: 2), x: 3, y: 5, Secret(4));

        // Assert
        Assert.Equal(ReestablishOutcome.DataLoss, plan.Outcome);
        Assert.Equal("B2-RE-23", plan.RequirementId);
        Assert.False(plan.MustBroadcast);
        Assert.Empty(plan.Steps);
    }

    [Fact]
    public void Given_PeerAheadWithoutProof_When_Planning_Then_FailNotDataLoss()
    {
        // Act
        var plan = Plan(State(l: 2, r: 2), x: 3, y: 5, Secret(3));

        // Assert
        Assert.Equal(ReestablishOutcome.Fail, plan.Outcome);
        Assert.Equal("B2-RE-21", plan.RequirementId);
    }

    [Theory]
    [InlineData(2UL, 0)]
    [InlineData(2UL, 5)]
    [InlineData(0UL, 1)]
    public void Given_WrongSecret_When_Planning_Then_Fail(ulong y, int secretNumber)
    {
        // Arrange (B2-RE-24): y = 2 needs our secret 1; y = 0 needs zeroes
        var local = State(l: 2, r: 2);
        if (y == 0)
            local = State(l: 0, r: 0);

        // Act
        var plan = Plan(local, x: y == 0 ? 1UL : 3UL, y, Secret((ulong)secretNumber));

        // Assert
        Assert.Equal(ReestablishOutcome.Fail, plan.Outcome);
        Assert.Equal("B2-RE-24", plan.RequirementId);
    }

    [Fact]
    public void Given_ShortSecret_When_Planning_Then_Fail()
    {
        // Act
        var plan = Plan(State(l: 0, r: 0), x: 1, y: 0, new byte[31]);

        // Assert
        Assert.Equal(ReestablishOutcome.Fail, plan.Outcome);
    }

    [Fact]
    public void Given_RevocationNumberBeyond48Bits_When_Planning_Then_FailWithoutAskingForTheSecret()
    {
        // Arrange
        var asked = false;

        // Act
        var plan = ReestablishPlanner.Plan(State(l: 2, r: 2),
                                           new PeerReestablish(3, ulong.MaxValue, Secret(1)),
                                           (_, _) => asked = true);

        // Assert
        Assert.Equal(ReestablishOutcome.Fail, plan.Outcome);
        Assert.False(asked);
    }

    [Fact]
    public void Given_NextFundingOnV1_When_Planning_Then_TxAbortFirst()
    {
        // Act (B2-RE-25)
        var plan = ReestablishPlanner.Plan(State(l: 2, r: 2), new PeerReestablish(3, 2, Secret(1), true), IsSecret);

        // Assert
        Assert.Equal(ReestablishOutcome.Resume, plan.Outcome);
        Assert.Equal([ReestablishStep.TxAbort], plan.Steps);
    }

    [Fact]
    public void Given_UnsignedLocalUpdates_When_Planning_Then_TheyFollowTheRetransmissions()
    {
        // Act
        var plan = Plan(State(l: 2, r: 2, hasNext: true, unsigned: true), x: 3, y: 1, Secret(0));

        // Assert
        Assert.Equal([ReestablishStep.RevokeAndAck, ReestablishStep.CommitDiff, ReestablishStep.UnsignedUpdates],
                     plan.Steps);
    }

    #endregion

    #region Exhaustive table

    /// <summary>
    /// Every (L, R, pending commitment, last sent, unsigned updates) x (X, Y, S in {ours, zeroes, other}) against an
    /// oracle written from the spec text: numbers of the last/next commitment_signed and revoke_and_ack.
    /// </summary>
    [Fact]
    public void Given_EveryStateAndReestablish_When_Planning_Then_ResultMatchesTheSpecOracle()
    {
        var checkedCases = 0;
        foreach (var l in Range(0, 4))
            foreach (var r in Range(0, 4))
                foreach (var hasNext in new[] { false, true })
                    foreach (var lastSent in new[] { LastSentCommitmentMessage.CommitmentSigned, LastSentCommitmentMessage.RevokeAndAck })
                        foreach (var unsigned in new[] { false, true })
                            foreach (var x in Range(0, r + 4))
                                foreach (var y in Range(0, l + 3))
                                    foreach (var secretKind in new[] { SecretKind.Ours, SecretKind.Zeroes, SecretKind.Other })
                                    {
                                        var local = State(l, r, hasNext, true, lastSent, unsigned);
                                        var secret = secretKind switch
                                        {
                                            SecretKind.Ours => y == 0 ? s_zeroes : Secret(y - 1),
                                            SecretKind.Zeroes => s_zeroes,
                                            _ => Secret(1_000 + y)
                                        };

                                        var actual = Plan(local, x, y, secret);
                                        var expected = Oracle(local, x, y, secret);

                                        Assert.True(expected.Outcome == actual.Outcome,
                                                    $"L={l} R={r} next={hasNext} last={lastSent} X={x} Y={y} S={secretKind}: expected {expected.Outcome}, got {actual.Outcome} ({actual.Reason})");
                                        if (expected.Outcome == ReestablishOutcome.Resume)
                                            Assert.True(expected.Steps.SequenceEqual(actual.Steps),
                                                        $"L={l} R={r} next={hasNext} last={lastSent} unsigned={unsigned} X={x} Y={y}: expected [{string.Join(",", expected.Steps)}], got [{string.Join(",", actual.Steps)}]");
                                        if (x == 0)
                                            Assert.True(actual.MustBroadcast);

                                        checkedCases++;
                                    }

        Assert.True(checkedCases > 4_000, $"{checkedCases} cases");
    }

    private enum SecretKind
    {
        Ours,
        Zeroes,
        Other
    }

    /// <summary>The BOLT 2 requirements, phrased as the spec does.</summary>
    private static ReestablishPlan Oracle(ReestablishLocalState local, ulong x, ulong y, byte[] secret)
    {
        var l = local.LocalCommitmentNumber;
        var r = local.RemoteCommitmentNumber;

        // "if next_commitment_number is zero: MUST immediately fail the channel and broadcast"
        if (x == 0)
            return ReestablishPlan.Failed("", "", mustBroadcast: true);

        // "the commitment number of the last revoke_and_ack the receiving node sent": we revoked l - 1 (none at l = 0)
        ulong? lastRevokeAndAck = l == 0 ? null : l - 1;
        var expectedY = l; // one greater than the last revoke_and_ack we sent (0 when none)

        // "if next_revocation_number is greater than expected above, AND your_last_per_commitment_secret is correct for
        // that next_revocation_number minus 1": data loss; otherwise the secret must match
        var secretCorrect = y == 0 ? secret.SequenceEqual(s_zeroes) : secret.SequenceEqual(Secret(y - 1));
        if (y > expectedY)
            return secretCorrect ? ReestablishPlan.LostData("") : ReestablishPlan.Failed("", "");
        if (!secretCorrect)
            return ReestablishPlan.Failed("", "");

        var resendRevokeAndAck = y == lastRevokeAndAck;
        if (!resendRevokeAndAck && y != expectedY)
            return ReestablishPlan.Failed("", "");

        // "if next_commitment_number is equal to the commitment number of the last commitment_signed the receiving node
        // has sent" (only one still unacked matters: an acked one can't be asked for again)
        ulong? lastUnackedCommitmentSigned = local.HasRemoteNextCommit ? r + 1 : null;
        var nextCommitmentSigned = local.HasRemoteNextCommit ? r + 2 : r + 1;
        var resendCommitmentSigned = x == lastUnackedCommitmentSigned;
        if (!resendCommitmentSigned && x != nextCommitmentSigned)
            return ReestablishPlan.Failed("", "");

        var steps = new List<ReestablishStep>();
        if (x == 1 && l + 1 == 1)
            steps.Add(ReestablishStep.ChannelReady);

        // "retransmit revoke_and_ack and commitment_signed in the same relative order as initially transmitted"
        var both = new List<ReestablishStep>();
        if (resendRevokeAndAck)
            both.Add(ReestablishStep.RevokeAndAck);
        if (resendCommitmentSigned)
            both.Add(ReestablishStep.CommitDiff);
        if (both.Count == 2 && local.LastSent == LastSentCommitmentMessage.RevokeAndAck)
            both.Reverse();
        steps.AddRange(both);

        if (local.HasUnsignedLocalUpdates)
            steps.Add(ReestablishStep.UnsignedUpdates);

        return ReestablishPlan.Resume(steps);
    }

    #endregion

    private static IEnumerable<ulong> Range(ulong from, ulong toExclusive)
    {
        for (var i = from; i < toExclusive; i++)
            yield return i;
    }

    private static ReestablishLocalState State(ulong l, ulong r, bool hasNext = false, bool hasDiff = true,
                                               LastSentCommitmentMessage lastSent =
                                                   LastSentCommitmentMessage.CommitmentSigned,
                                               bool unsigned = false) =>
        new(l, r, hasNext, hasNext && hasDiff, lastSent, unsigned);

    private static ReestablishPlan Plan(ReestablishLocalState local, ulong x, ulong y, byte[] secret) =>
        ReestablishPlanner.Plan(local, new PeerReestablish(x, y, secret), IsSecret);

    /// <summary>Our fake secret of commitment <paramref name="number"/>.</summary>
    private static byte[] Secret(ulong number)
    {
        var bytes = new byte[32];
        BitConverter.GetBytes(number + 1).CopyTo(bytes, 0);
        bytes[31] = 0x5E;
        return bytes;
    }

    private static bool IsSecret(ulong number, ReadOnlyMemory<byte> secret)
    {
        Assert.True(number <= PerCommitmentIndex.MaxCommitmentNumber);
        return secret.Span.SequenceEqual(Secret(number));
    }
}