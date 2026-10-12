namespace NLightning.Domain.Tests.Channels.Closing;

using Domain.Channels.Closing;

/// <summary>
/// The legacy <c>closing_signed</c> negotiation (BOLT 2 §6.11 rows B2-CLS-01..R09, B2-RE-29): named cases per
/// requirement, then two-party property runs with and without <c>fee_range</c>.
/// </summary>
public class LegacyClosingNegotiatorTests
{
    private static ClosingNegotiation Funder(ulong min, ulong max, bool sendRange = true) =>
        new() { IsFunder = true, Acceptable = new ClosingFeeRange(min, max), SendFeeRange = sendRange };

    private static ClosingNegotiation NonFunder(ulong min, ulong max, bool sendRange = true) =>
        new() { IsFunder = false, Acceptable = new ClosingFeeRange(min, max), SendFeeRange = sendRange };

    #region Named cases

    [Fact]
    public void Given_Funder_Cleared_When_Open_Then_ProposesEstimateWithRange()
    {
        // B2-CLS-01, B2-CLS-02
        // Act
        var (decision, next) = LegacyClosingNegotiator.Open(Funder(100, 1_000), 400);

        // Assert
        Assert.Equal(ClosingDecisionKind.Propose, decision.Kind);
        Assert.Equal(400UL, decision.FeeSat);
        Assert.Equal(new ClosingFeeRange(100, 1_000), decision.FeeRange);
        Assert.Equal(400UL, next.LastSentFeeSat);
        Assert.Equal(decision.FeeRange, next.LastSentRange);
    }

    [Fact]
    public void Given_EstimateAboveMax_When_Open_Then_ClampedAndNoRangeWhenDisabled()
    {
        // Act
        var (decision, _) = LegacyClosingNegotiator.Open(Funder(100, 1_000, sendRange: false), 5_000);

        // Assert
        Assert.Equal(1_000UL, decision.FeeSat);
        Assert.Null(decision.FeeRange);
    }

    [Fact]
    public void Given_EqualFee_When_Receive_Then_AgreeWithoutReply()
    {
        // B2-CLS-R02
        // Arrange
        var (_, state) = LegacyClosingNegotiator.Open(Funder(100, 1_000), 400);

        // Act
        var (decision, _) = LegacyClosingNegotiator.Receive(state, 400, new ClosingFeeRange(300, 500), 400);

        // Assert
        Assert.Equal(ClosingDecisionKind.Agree, decision.Kind);
        Assert.Equal(400UL, decision.FeeSat);
        Assert.False(decision.Reply);
    }

    [Fact]
    public void Given_FeeInOurRange_When_Receive_Then_EchoAndAgree()
    {
        // B2-CLS-R03
        // Arrange
        var (_, state) = LegacyClosingNegotiator.Open(Funder(100, 1_000), 400);

        // Act
        var (decision, next) = LegacyClosingNegotiator.Receive(state, 650, new ClosingFeeRange(600, 2_000), 400);

        // Assert
        Assert.Equal(ClosingDecisionKind.Agree, decision.Kind);
        Assert.True(decision.Reply);
        Assert.Equal(650UL, decision.FeeSat);
        Assert.Equal(650UL, next.LastSentFeeSat);
    }

    [Fact]
    public void Given_NoOverlap_When_Receive_Then_Warning()
    {
        // B2-CLS-R04 (non-funder, nothing sent yet)
        // Arrange
        var state = NonFunder(100, 200) with { Acceptable = new ClosingFeeRange(5_000, 10_000) };

        // Act
        var (decision, _) = LegacyClosingNegotiator.Receive(state, 300, new ClosingFeeRange(100, 1_000), 300);

        // Assert
        Assert.Equal(ClosingDecisionKind.Warn, decision.Kind);
        Assert.Equal("B2-CLS-R04", decision.RequirementId);
        Assert.False(decision.CloseConnection);
    }

    [Fact]
    public void Given_FunderFeeOutsideOverlap_When_Receive_Then_Fail()
    {
        // B2-CLS-R05: our range [100, 1000] was sent without the fee inside it
        // Arrange
        var state = Funder(100, 1_000, sendRange: false) with { LastSentFeeSat = 400 };

        // Act: their range overlaps ours on [800, 1000], but the fee 1500 is outside it
        var (decision, _) = LegacyClosingNegotiator.Receive(state, 1_500, new ClosingFeeRange(800, 2_000), 400);

        // Assert
        Assert.Equal(ClosingDecisionKind.Fail, decision.Kind);
        Assert.Equal("B2-CLS-R05", decision.RequirementId);
    }

    [Fact]
    public void Given_FunderFeeInsideOverlap_When_Receive_Then_ReplySameFee()
    {
        // B2-CLS-R05
        // Arrange
        var state = Funder(100, 1_000, sendRange: false) with { LastSentFeeSat = 400 };

        // Act
        var (decision, _) = LegacyClosingNegotiator.Receive(state, 900, new ClosingFeeRange(800, 2_000), 400);

        // Assert
        Assert.Equal(ClosingDecisionKind.Agree, decision.Kind);
        Assert.True(decision.Reply);
        Assert.Equal(900UL, decision.FeeSat);
    }

    [Fact]
    public void Given_NonFunder_When_Receive_Then_MaxAtLeastReceivedMax()
    {
        // B2-CLS-04: our max rises to the funder's max
        // Arrange
        var state = NonFunder(50, 500);

        // Act
        var (decision, _) = LegacyClosingNegotiator.Receive(state, 800, new ClosingFeeRange(700, 900), 300);

        // Assert
        Assert.Equal(ClosingDecisionKind.Agree, decision.Kind);
        Assert.Equal(800UL, decision.FeeSat);
        Assert.Equal(new ClosingFeeRange(50, 900), decision.FeeRange);
        Assert.Equal(new ClosingFeeRange(50, 900),
                     LegacyClosingNegotiator.OurRangeAgainst(state, new ClosingFeeRange(700, 900)));
    }

    [Fact]
    public void Given_NonFunderFeeBelowOurMin_When_Receive_Then_ProposeInOverlap()
    {
        // Arrange
        var state = NonFunder(600, 5_000);

        // Act: the funder offers 400 in [300, 900]; the overlap is [600, 900]
        var (decision, next) = LegacyClosingNegotiator.Receive(state, 400, new ClosingFeeRange(300, 900), 700);

        // Assert
        Assert.Equal(ClosingDecisionKind.Propose, decision.Kind);
        Assert.Equal(600UL, decision.FeeSat);
        Assert.Equal(600UL, next.LastSentFeeSat);
    }

    [Fact]
    public void Given_NonFunderMismatch_When_Receive_Then_Fail()
    {
        // B2-CLS-R06: the non-funder already proposed 600, the funder answers 650
        // Arrange
        var state = NonFunder(600, 5_000) with
        {
            LastSentFeeSat = 600,
            LastSentRange = new ClosingFeeRange(600, 5_000)
        };

        // Act: 650 is inside our sent range, so R03 applies first: agreement
        var (inRange, _) = LegacyClosingNegotiator.Receive(state, 650, new ClosingFeeRange(300, 900), 700);
        var outside = state with { LastSentRange = new ClosingFeeRange(600, 620) };
        var (decision, _) = LegacyClosingNegotiator.Receive(outside, 650, new ClosingFeeRange(300, 900), 700);

        // Assert
        Assert.Equal(ClosingDecisionKind.Agree, inRange.Kind);
        Assert.Equal(ClosingDecisionKind.Fail, decision.Kind);
        Assert.Equal("B2-CLS-R06", decision.RequirementId);
    }

    [Fact]
    public void Given_NotStrictlyBetween_When_ReceiveWithoutRange_Then_WarningAndClose()
    {
        // B2-CLS-R07: we sent 500, they sent 1000 before; 1200 is not between
        // Arrange
        var state = Funder(100, 600, sendRange: false) with { LastSentFeeSat = 500, LastReceivedFeeSat = 1_000 };

        // Act
        var (decision, _) = LegacyClosingNegotiator.Receive(state, 1_200, null, 500);

        // Assert
        Assert.Equal(ClosingDecisionKind.Warn, decision.Kind);
        Assert.True(decision.CloseConnection);
        Assert.Equal("B2-CLS-R07", decision.RequirementId);
    }

    [Fact]
    public void Given_Reconnect_When_Receive_Then_NoStrictlyBetweenCheck()
    {
        // B2-RE-29: a new negotiation after a reconnection starts from a fresh state
        // Arrange
        var fresh = Funder(100, 600, sendRange: false);
        var (_, opened) = LegacyClosingNegotiator.Open(fresh, 500);

        // Act: the peer's first fee on the new connection is compared with nothing it sent before
        var (decision, _) = LegacyClosingNegotiator.Receive(opened, 1_200, null, 500);

        // Assert
        Assert.Equal(ClosingDecisionKind.Propose, decision.Kind);
        Assert.Equal(600UL, decision.FeeSat);
    }

    [Fact]
    public void Given_Agree_When_ReceiveWithoutRange_Then_Echo()
    {
        // B2-CLS-R08
        // Arrange
        var state = NonFunder(100, 10_000, sendRange: false);

        // Act
        var (decision, _) = LegacyClosingNegotiator.Receive(state, 800, null, 300);

        // Assert
        Assert.Equal(ClosingDecisionKind.Agree, decision.Kind);
        Assert.True(decision.Reply);
        Assert.Equal(800UL, decision.FeeSat);
        Assert.Null(decision.FeeRange);
    }

    [Fact]
    public void Given_Disagree_When_ReceiveWithoutRange_Then_StrictlyBetween()
    {
        // B2-CLS-R09
        // Arrange
        var state = Funder(100, 900, sendRange: false) with { LastSentFeeSat = 500 };

        // Act
        var (decision, _) = LegacyClosingNegotiator.Receive(state, 1_500, null, 500);

        // Assert
        Assert.Equal(ClosingDecisionKind.Propose, decision.Kind);
        Assert.Equal(900UL, decision.FeeSat); // midpoint 1000 clamped to our max
    }

    [Fact]
    public void Given_NoFeeStrictlyBetweenLeft_When_Receive_Then_HoldsLastFee()
    {
        // B2-CLS-R09 (SHOULD): we are at our max already, so we re-send it instead of failing the channel
        // Arrange
        var state = Funder(100, 900, sendRange: false) with
        {
            LastSentFeeSat = 900,
            LastReceivedFeeSat = 2_000,
            Rounds = 1
        };

        // Act
        var (decision, next) = LegacyClosingNegotiator.Receive(state, 1_500, null, 500);

        // Assert
        Assert.Equal(ClosingDecisionKind.Propose, decision.Kind);
        Assert.Equal(900UL, decision.FeeSat);
        Assert.Equal("B2-CLS-R09", decision.RequirementId);
        Assert.Equal(900UL, next.LastSentFeeSat);
    }

    [Fact]
    public void Given_AtOurLimitAndPeerNotSeenConverging_When_Receive_Then_WarnAndCloseConnection()
    {
        // B2-CLS-R09 MUST (propose strictly between): our opening fee is already our limit and the peer's first fee is
        // beyond it, so we can't move and the peer has not shown it moves towards us: warning and close, no hold
        // Arrange
        var state = Funder(100, 900, sendRange: false) with { LastSentFeeSat = 900, Rounds = 0 };

        // Act
        var (decision, next) = LegacyClosingNegotiator.Receive(state, 1_500, null, 900);

        // Assert
        Assert.Equal(ClosingDecisionKind.Warn, decision.Kind);
        Assert.True(decision.CloseConnection);
        Assert.Equal("B2-CLS-R09", decision.RequirementId);
        Assert.Equal(900UL, next.LastSentFeeSat);
    }

    [Fact]
    public void Given_HeldTooLong_When_Receive_Then_WarnAndCloseConnection()
    {
        // Arrange
        var state = Funder(100, 900, sendRange: false) with
        {
            LastSentFeeSat = 900,
            LastReceivedFeeSat = 2_000,
            Rounds = LegacyClosingNegotiator.MaxRounds
        };

        // Act
        var (decision, _) = LegacyClosingNegotiator.Receive(state, 1_500, null, 500);

        // Assert
        Assert.Equal(ClosingDecisionKind.Warn, decision.Kind);
        Assert.True(decision.CloseConnection);
        Assert.Equal("B2-CLS-R09", decision.RequirementId);
    }

    [Fact]
    public void Given_PeerLowersByTenPercentEachRound_When_WeHoldOurMax_Then_PeerReachesOurFee()
    {
        // The LND pattern seen in the Docker proof: the non-funder starts at 4225 sat and lowers by 10 % per round
        // while our (funder) limit is 513 sat
        // Arrange
        var (_, state) = LegacyClosingNegotiator.Open(Funder(171, 513, sendRange: false), 171);
        ulong peerFee = 4_225;
        ClosingDecision? decision = null;

        // Act
        for (var round = 0; round < 40; round++)
        {
            (decision, state) = LegacyClosingNegotiator.Receive(state, peerFee, null, 171);
            if (decision.Kind != ClosingDecisionKind.Propose)
                break;
            peerFee = Math.Max(decision.FeeSat, peerFee - peerFee / 10);
        }

        // Assert
        Assert.NotNull(decision);
        Assert.Equal(ClosingDecisionKind.Agree, decision.Kind);
        Assert.Equal(513UL, decision.FeeSat);
    }

    #endregion

    #region Two-party runs

    public static TheoryData<int> Seeds()
    {
        var data = new TheoryData<int>();
        for (var seed = 0; seed < 200; seed++)
            data.Add(seed);
        return data;
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Given_OverlappingPreferences_When_NegotiateWithoutRange_Then_ConvergesWithin64Rounds(int seed)
    {
        // Arrange
        var random = new Random(seed);
        var (funder, nonFunder, funderIdeal, nonFunderIdeal) = RandomOverlapping(random, sendRange: false);

        // Act
        var result = Run(funder, nonFunder, funderIdeal, nonFunderIdeal);

        // Assert
        Assert.True(result.Agreed, $"seed {seed}: {result.Failure}");
        Assert.True(result.Messages <= 64, $"seed {seed}: {result.Messages} messages");
        Assert.True(funder.Acceptable.Contains(result.Fee) && nonFunder.Acceptable.Contains(result.Fee),
                    $"seed {seed}: fee {result.Fee} outside {funder.Acceptable} or {nonFunder.Acceptable}");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Given_OverlappingRanges_When_NegotiateWithRange_Then_ConvergesWithinThreeMessages(int seed)
    {
        // Arrange
        var random = new Random(seed);
        var (funder, nonFunder, funderIdeal, nonFunderIdeal) = RandomOverlapping(random, sendRange: true);

        // Act
        var result = Run(funder, nonFunder, funderIdeal, nonFunderIdeal);

        // Assert
        Assert.True(result.Agreed, $"seed {seed}: {result.Failure}");
        Assert.True(result.Messages <= 3, $"seed {seed}: {result.Messages} messages");
        Assert.True(funder.Acceptable.Contains(result.Fee), $"seed {seed}: {result.Fee} not in {funder.Acceptable}");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Given_MixedRangeUse_When_Negotiate_Then_Converges(int seed)
    {
        // Arrange: one side sends fee_range, the other does not
        var random = new Random(seed);
        var (funder, nonFunder, funderIdeal, nonFunderIdeal) = RandomOverlapping(random, sendRange: true);
        if (seed % 2 == 0)
            funder = funder with { SendFeeRange = false };
        else
            nonFunder = nonFunder with { SendFeeRange = false };

        // Act
        var result = Run(funder, nonFunder, funderIdeal, nonFunderIdeal);

        // Assert
        Assert.True(result.Agreed, $"seed {seed}: {result.Failure}");
        Assert.True(result.Messages <= 64, $"seed {seed}: {result.Messages} messages");
    }

    [Fact]
    public void Given_DisjointPreferences_When_NegotiateWithoutRange_Then_FailsInsteadOfLooping()
    {
        // Arrange
        var funder = Funder(100, 500, sendRange: false);
        var nonFunder = NonFunder(1_000, 5_000, sendRange: false);

        // Act
        var result = Run(funder, nonFunder, 300, 2_000);

        // Assert
        Assert.False(result.Agreed);
        Assert.True(result.Messages <= 64);
    }

    private static (ClosingNegotiation Funder, ClosingNegotiation NonFunder, ulong FunderIdeal, ulong NonFunderIdeal)
        RandomOverlapping(Random random, bool sendRange)
    {
        var funderMin = (ulong)random.Next(1, 50_000);
        var funderMax = funderMin + (ulong)random.Next(0, 1_000_000);
        // The non-funder's range overlaps the funder's
        var nonFunderMin = (ulong)random.NextInt64((long)Math.Max(1, funderMin / 2), (long)funderMax + 1);
        var nonFunderMax = nonFunderMin + (ulong)random.Next(0, 2_000_000);
        var funderIdeal = (ulong)random.NextInt64(1, 2_000_000);
        var nonFunderIdeal = (ulong)random.NextInt64(1, 2_000_000);
        return (Funder(funderMin, funderMax, sendRange), NonFunder(nonFunderMin, nonFunderMax, sendRange),
                funderIdeal, nonFunderIdeal);
    }

    private sealed record RunResult(bool Agreed, ulong Fee, int Messages, string? Failure);

    /// <summary>
    /// Plays both sides: the funder opens, each message is delivered to the other side, until one side agrees without
    /// replying (the other already agreed on the same fee) or someone warns/fails.
    /// </summary>
    private static RunResult Run(ClosingNegotiation funder, ClosingNegotiation nonFunder, ulong funderIdeal,
                                 ulong nonFunderIdeal)
    {
        var (opening, funderState) = LegacyClosingNegotiator.Open(funder, funderIdeal);
        var nonFunderState = nonFunder;
        var inFlight = opening;
        var toNonFunder = true;
        var messages = 1;
        ulong? agreedByOther = null;

        while (messages <= 200)
        {
            var receiverState = toNonFunder ? nonFunderState : funderState;
            var (decision, next) = LegacyClosingNegotiator.Receive(receiverState, inFlight.FeeSat, inFlight.FeeRange,
                                                                   toNonFunder ? nonFunderIdeal : funderIdeal);
            if (toNonFunder)
                nonFunderState = next;
            else
                funderState = next;

            switch (decision.Kind)
            {
                case ClosingDecisionKind.Warn or ClosingDecisionKind.Fail:
                    return new RunResult(false, 0, messages, $"{decision.RequirementId}: {decision.Reason}");
                case ClosingDecisionKind.Agree when !decision.Reply:
                    return new RunResult(true, decision.FeeSat, messages, null);
                case ClosingDecisionKind.Agree:
                    // The receiver closes at this fee; its echo must end the negotiation on the other side
                    if (agreedByOther is { } other && other != decision.FeeSat)
                        return new RunResult(false, 0, messages, $"agreed on {other} and {decision.FeeSat}");
                    agreedByOther = decision.FeeSat;
                    break;
            }

            inFlight = decision;
            toNonFunder = !toNonFunder;
            messages++;
        }

        return new RunResult(false, 0, messages, "no convergence");
    }

    #endregion
}