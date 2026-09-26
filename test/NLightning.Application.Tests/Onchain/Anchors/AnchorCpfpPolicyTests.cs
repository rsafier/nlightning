namespace NLightning.Application.Tests.Onchain.Anchors;

using Application.Onchain.Anchors;
using Domain.Onchain.Fees;

/// <summary>
/// BOLT 5 plan O7-T2: the fee rules of the anchor CPFP (<see cref="AnchorCpfpPolicy"/>): a first child only while the
/// commitment pays less than the estimate, the package at the estimate, a BIP 125 replacement, the cap, and the anchor
/// sweep economics.
/// </summary>
public class AnchorCpfpPolicyTests
{
    private readonly AnchorCpfpPolicy _policy = new(new SweepFeePolicy());

    [Fact]
    public void Given_CommitmentPayingTheEstimate_When_Deciding_Then_NoChild()
    {
        // Act: 2,500 sat/kw paid, 2,000 wanted
        var decision = _policy.DecideChild(2_500, 1_000, 700, 2_000, 100_000);

        // Assert
        Assert.Null(decision);
    }

    [Fact]
    public void Given_CommitmentBelowTheEstimate_When_Deciding_Then_PackageReachesTheEstimate()
    {
        // Act: commitment 1,000 WU paying 253 sat, estimate 10,000 sat/kw, child 700 WU
        var decision = _policy.DecideChild(253, 1_000, 700, 10_000, 100_000);

        // Assert: ceil(10,000 * 1,700 / 1000) - 253 = 16,747
        Assert.NotNull(decision);
        Assert.Equal(16_747UL, decision.FeeSat);
        Assert.Equal(10_000u, decision.TargetFeeratePerKw);
        Assert.True(decision.PackageFeeratePerKw >= 10_000);
        Assert.False(decision.Capped);
    }

    [Fact]
    public void Given_EstimateAboveTheCap_When_Deciding_Then_FeeCapped()
    {
        // Act
        var decision = _policy.DecideChild(253, 1_000, 700, 100_000, 20_000);

        // Assert
        Assert.NotNull(decision);
        Assert.Equal(20_000UL, decision.FeeSat);
        Assert.True(decision.Capped);
    }

    [Fact]
    public void Given_EstimateBelowTheFloor_When_Deciding_Then_FloorApplies()
    {
        // Act: a commitment paying 100 sat/kw with an estimate of 0 still needs the 253 floor
        var decision = _policy.DecideChild(100, 1_000, 700, 0, 100_000);

        // Assert: ceil(253 * 1,700 / 1000) - 100 = 331
        Assert.NotNull(decision);
        Assert.Equal(331UL, decision.FeeSat);
        Assert.Equal(253u, decision.TargetFeeratePerKw);
    }

    [Fact]
    public void Given_OldChild_When_Replacing_Then_FeeMeetsBip125AndTheNewEstimate()
    {
        // Act: old child fee 16,747; the estimate stays at 10,000 (only the BIP 125 minimum raises it)
        var sameEstimate = _policy.DecideReplacement(253, 1_000, 700, 10_000, 16_747, 100_000);

        // ... and the estimate doubles
        var higher = _policy.DecideReplacement(253, 1_000, 700, 20_000, 16_747, 100_000);

        // Assert: x1.25 of 16,747 = 20,934 (> 16,747 + 175 vB relay)
        Assert.NotNull(sameEstimate);
        Assert.Equal(20_934UL, sameEstimate.FeeSat);
        Assert.NotNull(higher);
        Assert.Equal(20_000UL * 1_700 / 1_000 - 253, higher.FeeSat);
    }

    [Fact]
    public void Given_CapBelowTheBip125Minimum_When_Replacing_Then_NoReplacement()
    {
        // Act
        var decision = _policy.DecideReplacement(253, 1_000, 700, 50_000, 16_747, 18_000);

        // Assert
        Assert.Null(decision);
    }

    [Fact]
    public void Given_ChildPackage_When_CheckingTheEstimate_Then_PaysOnlyAtOrAboveIt()
    {
        // Assert: (253 + 16,747) * 1000 / 1,700 = 10,000
        Assert.True(_policy.PackagePays(253, 1_000, 16_747, 700, 10_000));
        Assert.False(_policy.PackagePays(253, 1_000, 16_747, 700, 10_001));
    }

    [Fact]
    public void Given_Htlcs_When_GettingTheDeadline_Then_EarliestExpiry()
    {
        // Assert
        Assert.Equal(580u, AnchorCpfpPolicy.GetDeadline([600, 580, 700]));
        Assert.Null(AnchorCpfpPolicy.GetDeadline([]));
        Assert.Equal(36u, _policy.GetConfirmationTarget(500, null));
        Assert.Equal(77u, _policy.GetConfirmationTarget(500, 580));
    }

    [Fact]
    public void Given_Stake_When_GettingTheCap_Then_HalfOfItWithAFloor()
    {
        // Assert
        Assert.Equal(400_000UL, _policy.GetFeeCap(800_000));
        Assert.Equal(20_000UL, _policy.GetFeeCap(1_000));
    }

    [Theory]
    [InlineData(1, 253, false)] // 330 - 94 = 236 sat, below the 294-sat P2WPKH dust limit
    [InlineData(2, 253, true)] // 660 - 146 = 514 sat
    [InlineData(2, 600, false)] // a 348 sat fee is more than half of 660
    [InlineData(2, 2_500, false)]
    public void Given_Anchors_When_DecidingTheSweep_Then_OnlyWhenItPaysForItself(int count, uint estimate,
                                                                                 bool economical)
    {
        // Arrange: the sweep weight of the builder (42 + count * 207 + 124)
        var weight = 42 + count * 207L + 124;

        // Act
        var decision = _policy.DecideAnchorSweep(count, weight, estimate, 294);

        // Assert
        Assert.Equal(economical, decision.Economical);
    }
}