namespace NLightning.Application.Tests.Channels.Fees;

using Application.Channels.Fees;
using Domain.Node.Options;
using static FeeTestKit;

/// <summary>
/// BOLT2 plan N9-T1: when the funder sends <c>update_fee</c> (B2-FEE-S01..S03, B2-DUST-05).
/// </summary>
public class FeeUpdatePolicyTests
{
    private static readonly FeeUpdateOptions s_options = new();

    [Fact]
    public void Given_EstimateUp20Pct_When_Deciding_Then_UpdateToEstimate()
    {
        // Arrange
        var commitments = Create(800_000, 200_000, 2_500);

        // Act
        var decision = FeeUpdatePolicy.Decide(commitments, 3_000, s_options, null);

        // Assert
        Assert.True(decision.ShouldSend);
        Assert.Equal(3_000U, decision.FeeratePerKw);
        Assert.Equal(2_500U, decision.CurrentFeeratePerKw);
        Assert.Null(decision.Reason);
    }

    [Theory]
    [InlineData(2_999U)]
    [InlineData(2_001U)]
    [InlineData(2_500U)]
    public void Given_EstimateWithinThreshold_When_Deciding_Then_NoUpdate(uint estimate)
    {
        // Arrange
        var commitments = Create(800_000, 200_000, 2_500);

        // Act
        var decision = FeeUpdatePolicy.Decide(commitments, estimate, s_options, null);

        // Assert
        Assert.False(decision.ShouldSend);
        Assert.Equal("within the threshold", decision.Reason);
    }

    [Fact]
    public void Given_EstimateDown20Pct_When_Deciding_Then_UpdateDown()
    {
        // Arrange
        var commitments = Create(800_000, 200_000, 2_500);

        // Act
        var decision = FeeUpdatePolicy.Decide(commitments, 2_000, s_options, null);

        // Assert
        Assert.Equal(2_000U, decision.FeeratePerKw);
    }

    [Fact]
    public void Given_EstimateBelowFloor_When_Deciding_Then_NeverBelow253()
    {
        // Arrange
        var commitments = Create(800_000, 200_000, 1_000);

        // Act
        var decision = FeeUpdatePolicy.Decide(commitments, 100, s_options, null);

        // Assert
        Assert.Equal(FeeUpdateOptions.FeeratePerKwFloor, decision.FeeratePerKw);
    }

    [Fact]
    public void Given_ConfiguredMinimumBelowFloor_When_Targeting_Then_FloorStillApplies()
    {
        // Arrange
        var options = new FeeUpdateOptions { MinFeeratePerKw = 100 };

        // Act
        var target = FeeUpdatePolicy.TargetFeeratePerKw(50, false, options);

        // Assert
        Assert.Equal(FeeUpdateOptions.FeeratePerKwFloor, target);
    }

    [Fact]
    public void Given_FeerateBelowFloor_When_EstimateBarelyHigher_Then_RaisedToFloor()
    {
        // Arrange - a channel opened below the relay floor is raised whatever the threshold says
        var commitments = Create(800_000, 200_000, 200);

        // Act
        var decision = FeeUpdatePolicy.Decide(commitments, 210, s_options, null);

        // Assert
        Assert.Equal(FeeUpdateOptions.FeeratePerKwFloor, decision.FeeratePerKw);
    }

    [Theory]
    [InlineData(false, 50_000U)]
    [InlineData(true, 2_500U)]
    public void Given_HugeEstimate_When_Deciding_Then_ClampedToTheMaximum(bool anchors, uint expected)
    {
        // Arrange
        var commitments = Create(800_000, 200_000, 1_000, anchors: anchors);

        // Act
        var decision = FeeUpdatePolicy.Decide(commitments, 1_000_000, s_options, null);

        // Assert
        Assert.Equal(expected, decision.FeeratePerKw);
    }

    [Fact]
    public void Given_NotTheFunder_When_Deciding_Then_NeverSends()
    {
        // Arrange - BOLT 2: the node not responsible for the fee MUST NOT send update_fee
        var commitments = Create(200_000, 800_000, 2_500, localIsFunder: false);

        // Act
        var decision = FeeUpdatePolicy.Decide(commitments, 10_000, s_options, null);

        // Assert
        Assert.False(decision.ShouldSend);
        Assert.Contains("B2-FEE-S02", decision.Reason);
    }

    [Fact]
    public void Given_NoEstimate_When_Deciding_Then_NoUpdate()
    {
        // Arrange
        var commitments = Create(800_000, 200_000, 2_500);

        // Act
        var decision = FeeUpdatePolicy.Decide(commitments, 0, s_options, null);

        // Assert
        Assert.False(decision.ShouldSend);
    }

    [Fact]
    public void Given_TargetAboveWhatWeCanAfford_When_Deciding_Then_CappedAtTheHighestAffordableFeerate()
    {
        // Arrange - 30,000 sat with a 10,000 sat reserve: the fee (724 * f / 1000) may take at most 20,000 sat, so
        // 27,625 sat/kw is the most the peer would accept from us (B2-FEE-R03)
        var commitments = Create(30_000, 970_000, 10_000);

        // Act
        var decision = FeeUpdatePolicy.Decide(commitments, 40_000, s_options, null);

        // Assert
        Assert.Equal(40_000U, decision.TargetFeeratePerKw);
        Assert.Equal(27_625U, decision.FeeratePerKw);
        Assert.Contains("B2-FEE-R03", decision.Reason);
        Assert.True(FeeUpdatePolicy.CanSend(commitments, 27_625, null, out _));
        Assert.False(FeeUpdatePolicy.CanSend(commitments, 27_626, null, out _));
    }

    [Fact]
    public void Given_ReserveAlreadyReached_When_EstimateRises_Then_NoUpdate()
    {
        // Arrange - 17,239 sat: the 7,240 sat fee at 10,000 sat/kw already eats into the 10,000 sat reserve
        var commitments = Create(17_239, 982_761, 10_000);

        // Act
        var decision = FeeUpdatePolicy.Decide(commitments, 20_000, s_options, null);

        // Assert
        Assert.False(decision.ShouldSend);
        Assert.Contains("cannot raise the feerate", decision.Reason);
    }

    [Fact]
    public void Given_ReserveAlreadyReached_When_EstimateFalls_Then_DecreaseIsSent()
    {
        // Arrange - lowering the fee only helps
        var commitments = Create(17_239, 982_761, 10_000);

        // Act
        var decision = FeeUpdatePolicy.Decide(commitments, 5_000, s_options, null);

        // Assert
        Assert.Equal(5_000U, decision.FeeratePerKw);
    }

    [Fact]
    public void Given_IncreaseTrimsHtlcsOverTheDustLimit_When_Deciding_Then_CappedBelowTheTrimPoint()
    {
        // Arrange - B2-DUST-05: two 7,000 sat HTLCs are untrimmed at 2,000 sat/kw. On our commitment they become
        // trimmed (546 + 703 * f / 1000 > 7,000) from 9,183 sat/kw on: 14,000 sat of dust against a 10,000 sat limit
        var commitments = Create(800_000, 200_000, 2_000,
                                 htlcs: [Incoming(0, 7_000), Incoming(1, 7_000)]);

        // Act
        var decision = FeeUpdatePolicy.Decide(commitments, 10_000, s_options, 10_000 * Sat);

        // Assert
        Assert.Equal(9_182U, decision.FeeratePerKw);
        Assert.Contains("B2-DUST-05", decision.Reason);
    }

    [Fact]
    public void Given_NoDustLimit_When_IncreaseTrimsHtlcs_Then_TargetIsSent()
    {
        // Arrange
        var commitments = Create(800_000, 200_000, 2_000,
                                 htlcs: [Incoming(0, 7_000), Incoming(1, 7_000)]);

        // Act
        var decision = FeeUpdatePolicy.Decide(commitments, 10_000, s_options, null);

        // Assert
        Assert.Equal(10_000U, decision.FeeratePerKw);
    }

    [Theory]
    [InlineData(20U, 2_500U, 3_000U, true)]
    [InlineData(20U, 2_500U, 2_999U, false)]
    [InlineData(20U, 2_500U, 2_000U, true)]
    [InlineData(20U, 2_500U, 2_001U, false)]
    [InlineData(10U, 2_500U, 2_750U, true)]
    [InlineData(20U, 2_500U, 2_500U, false)]
    public void Given_Threshold_When_Comparing_Then_HysteresisApplies(uint percent, uint current, uint target,
                                                                      bool expected)
    {
        // Arrange
        var options = new FeeUpdateOptions { ThresholdPercent = percent };

        // Act
        var adjust = FeeUpdatePolicy.ShouldAdjust(current, target, options);

        // Assert
        Assert.Equal(expected, adjust);
    }

    [Fact]
    public void Given_DefaultOptions_When_Validating_Then_NoErrors()
    {
        // Act
        var errors = new NodeOptions().GetValidationErrors();

        // Assert
        Assert.Empty(errors);
        Assert.Equal(NodeOptions.DefaultMaxDustHtlcExposureMsat, new NodeOptions().MaxDustHtlcExposureMsat);
    }

    [Fact]
    public void Given_BadFeeUpdateOptions_When_Validating_Then_EveryErrorIsReported()
    {
        // Arrange
        var options = new NodeOptions
        {
            FeeUpdates = new FeeUpdateOptions
            {
                Interval = TimeSpan.Zero,
                ThresholdPercent = 0,
                MinFeeratePerKw = 100,
                MaxFeeratePerKw = 50,
                MaxAnchorFeeratePerKw = 50
            }
        };

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Equal(5, errors.Count);
        Assert.All(errors, e => Assert.StartsWith("FeeUpdates:", e));
    }
}