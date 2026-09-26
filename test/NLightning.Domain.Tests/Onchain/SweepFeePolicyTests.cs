namespace NLightning.Domain.Tests.Onchain;

using Domain.Channels.Enums;
using Domain.Onchain.Fees;

/// <summary>
/// BOLT 5 plan O6-T1 (§3.7): the <see cref="SweepFeePolicy"/> table: confirmation targets from deadlines, the 253
/// sat/kw floor, the 50 % / 100 % caps, dust abandonment, BIP 125 replacement fees, bump timing and the
/// <c>security_delay</c> penalty split.
/// </summary>
public class SweepFeePolicyTests
{
    private readonly SweepFeePolicy _policy = new();

    [Theory]
    [InlineData(1_000u, null, 36u)] // no deadline: the slow sweep target
    [InlineData(1_000u, 1_100u, 97u)] // 100 blocks left - 3 safety
    [InlineData(1_000u, 1_004u, 1u)] // 4 left - 3 = 1
    [InlineData(1_000u, 1_002u, 1u)] // inside the safety margin: fastest
    [InlineData(1_000u, 900u, 1u)] // already past: fastest
    [InlineData(1_000u, 5_000u, 144u)] // far away: capped at 144
    public void Given_Deadline_When_GettingTarget_Then_ClampedDeadlineMinusSafety(uint tip, uint? deadline,
                                                                                 uint expected)
    {
        // Act / Assert
        Assert.Equal(expected, _policy.GetConfirmationTarget(tip, deadline));
    }

    [Theory]
    [InlineData(0u, 253u)]
    [InlineData(252u, 253u)]
    [InlineData(253u, 253u)]
    [InlineData(5_000u, 5_000u)]
    public void Given_Estimate_When_Floored_Then_NeverBelow253(uint estimate, uint expected)
    {
        // Act / Assert
        Assert.Equal(expected, _policy.ApplyFloor(estimate));
    }

    [Fact]
    public void Given_NormalEstimate_When_Deciding_Then_FeeIsRateTimesWeight()
    {
        // Act: 1,000 sat/kw on 500 WU = 500 sat, well under half of 100,000
        var decision = _policy.Decide(100_000, 500, 1_000, false, 1_000, null);

        // Assert
        Assert.Equal(new SweepFeeDecision(1_000, 500, false, false), decision);
    }

    [Fact]
    public void Given_EstimateAboveHalfTheValue_When_DecidingSweep_Then_CappedAt50Percent()
    {
        // Act: 100,000 sat/kw on 500 WU = 50,000 sat for a 20,000 sat output
        var decision = _policy.Decide(20_000, 500, 100_000, false, 1_000, null);

        // Assert
        Assert.True(decision.Capped);
        Assert.Equal(10_000UL, decision.FeeSat);
        Assert.Equal(20_000U, decision.FeeratePerKw);
    }

    [Theory]
    [InlineData(1_100u, 10_000UL)] // deadline far: the sweep cap (50 %)
    [InlineData(1_018u, 20_000UL)] // within security_delay (18): up to 100 %
    public void Given_Penalty_When_NearItsDeadline_Then_MayPayTheWholeValue(uint deadline, ulong expectedFee)
    {
        // Act
        var decision = _policy.Decide(20_000, 500, 100_000, true, 1_000, deadline);

        // Assert
        Assert.True(decision.Capped);
        Assert.Equal(expectedFee, decision.FeeSat);
    }

    [Theory]
    [InlineData(100UL, true)] // 253 sat/kw * 500 WU = 126 sat > 100
    [InlineData(126UL, true)]
    [InlineData(127UL, false)]
    public void Given_ValueBelowFloorFee_When_Deciding_Then_Abandoned(ulong value, bool abandon)
    {
        // Act
        var decision = _policy.Decide(value, 500, 253, false, 1_000, null);

        // Assert
        Assert.Equal(abandon, decision.Abandon);
    }

    [Theory]
    [InlineData(1_000UL, 200L, 1_250UL)] // x1.25 dominates
    [InlineData(100UL, 200L, 300UL)] // old + 1 sat/vB * 200 vB dominates
    [InlineData(3UL, 1L, 4UL)] // rounding up
    public void Given_OldFee_When_Replacing_Then_Bip125Increment(ulong oldFee, long vsize, ulong expected)
    {
        // Act / Assert
        Assert.Equal(expected, _policy.GetReplacementFee(oldFee, vsize));
    }

    [Theory]
    [InlineData(1_000u, 1_001u, 1_100u, false)] // 1 block: wait
    [InlineData(1_000u, 1_002u, 1_100u, true)] // RbfIntervalBlocks (2)
    [InlineData(1_000u, 1_100u, 1_100u, false)] // deadline passed: nothing to win
    [InlineData(1_000u, 1_035u, null, false)] // no deadline: after the sweep target (36)
    [InlineData(1_000u, 1_036u, null, true)]
    [InlineData(1_010u, 1_000u, null, false)] // reorged below the broadcast
    public void Given_Unconfirmed_When_CheckingBump_Then_BumpedOnSchedule(uint broadcast, uint tip, uint? deadline,
                                                                         bool expected)
    {
        // Act / Assert
        Assert.Equal(expected, _policy.ShouldBump(broadcast, tip, deadline));
    }

    [Theory]
    [InlineData(1_000u, 1_019u, false)]
    [InlineData(1_000u, 1_018u, true)]
    [InlineData(1_000u, 990u, true)]
    public void Given_PenaltyDeadline_When_CheckingSplit_Then_SplitWithinSecurityDelay(uint tip, uint deadline,
                                                                                       bool expected)
    {
        // Act / Assert (B5-REV-08, security_delay 18)
        Assert.Equal(expected, _policy.ShouldSplitPenalty(tip, deadline));
    }

    [Theory]
    [InlineData(null, 1_144u)] // to_local / second level: confirmation + to_self_delay
    [InlineData(HtlcDirection.Incoming, 1_500u)] // their offered HTLC: its HTLC-timeout path from cltv_expiry
    [InlineData(HtlcDirection.Outgoing, 1_021u)] // our offered HTLC: its HTLC-success path at any time
    public void Given_RevokedOutput_When_GettingDeadline_Then_PerPlan(HtlcDirection? direction, uint expected)
    {
        // Act / Assert
        Assert.Equal(expected, SweepFeePolicy.GetRevokedOutputDeadline(direction, 1_000, 144, 1_500, 1_020));
    }

    [Fact]
    public void Given_CustomOptions_When_Deciding_Then_TheyApply()
    {
        // Arrange
        var policy = new SweepFeePolicy(new SweepFeePolicyOptions
        {
            MinFeeratePerKw = 1_000,
            SweepMaxFeePerMille = 100,
            SweepConfTarget = 6
        });

        // Act
        var decision = policy.Decide(10_000, 1_000, 5_000, false, 1_000, null);

        // Assert
        Assert.Equal(6U, policy.GetConfirmationTarget(1_000, null));
        Assert.Equal(1_000UL, decision.FeeSat);
        Assert.True(decision.Capped);
    }

    [Fact]
    public void Given_FeeAndWeight_When_ComputingRate_Then_RoundedDown()
    {
        // Act / Assert
        Assert.Equal(333U, SweepFeePolicy.FeeratePerKw(1, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => SweepFeePolicy.FeeratePerKw(1, 0));
    }
}