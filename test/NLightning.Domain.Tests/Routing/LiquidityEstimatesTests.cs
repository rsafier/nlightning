namespace NLightning.Domain.Tests.Routing;

using Domain.Channels.ValueObjects;
using Domain.Routing.Pathfinding;

public class LiquidityEstimatesTests
{
    private static readonly DirectedChannel s_edge = new(new ShortChannelId(1, 2, 3), 0);
    private const double Prior = 0.6;

    [Fact]
    public void Given_NoRecord_When_Asking_Then_Prior()
    {
        // Act
        var p = new LiquidityEstimates().GetSuccessProbability(s_edge, 1_000, 10_000, 0, Prior);

        // Assert
        Assert.Equal(Prior, p);
    }

    [Fact]
    public void Given_AmountAboveCapacity_When_Asking_Then_Zero()
    {
        // Act
        var p = new LiquidityEstimates().GetSuccessProbability(s_edge, 10_001, 10_000, 0, Prior);

        // Assert
        Assert.Equal(0, p);
    }

    [Fact]
    public void Given_FreshBounds_When_Asking_Then_UniformBetweenThem()
    {
        // Arrange: a success of 2,000 and a failure of 6,000
        var estimates = new LiquidityEstimates();
        estimates.RecordSuccess(s_edge, 2_000, 100);
        estimates.RecordFailure(s_edge, 6_000, 100);

        // Act / Assert
        Assert.Equal(1.0, estimates.GetSuccessProbability(s_edge, 1_999, 10_000, 100, Prior));
        Assert.Equal(1.0, estimates.GetSuccessProbability(s_edge, 2_000, 10_000, 100, Prior));
        Assert.Equal(0.5, estimates.GetSuccessProbability(s_edge, 4_000, 10_000, 100, Prior), 10);
        Assert.Equal(0.0, estimates.GetSuccessProbability(s_edge, 6_000, 10_000, 100, Prior));
    }

    [Fact]
    public void Given_OneHalfLife_When_Asking_Then_HalfwayBackToThePrior()
    {
        // Arrange
        var estimates = new LiquidityEstimates(TimeSpan.FromHours(1));
        estimates.RecordFailure(s_edge, 5_000, 0);

        // Act
        var p = estimates.GetSuccessProbability(s_edge, 5_000, 10_000, 3_600, Prior);

        // Assert: 0.5 * 0 + 0.5 * 0.6
        Assert.Equal(0.3, p, 10);
    }

    [Fact]
    public void Given_FailureBelowOldSuccess_When_Recording_Then_LowerBoundMovesBelowUpper()
    {
        // Arrange
        var estimates = new LiquidityEstimates();
        estimates.RecordSuccess(s_edge, 8_000, 0);

        // Act
        estimates.RecordFailure(s_edge, 3_000, 0);

        // Assert
        Assert.True(estimates.TryGetBounds(s_edge, out var min, out var max, out _));
        Assert.Equal(3_000UL, max);
        Assert.True(min < max);
    }

    [Fact]
    public void Given_SuccessAboveOldFailure_When_Recording_Then_UpperBoundDropped()
    {
        // Arrange
        var estimates = new LiquidityEstimates();
        estimates.RecordFailure(s_edge, 3_000, 0);

        // Act
        estimates.RecordSuccess(s_edge, 4_000, 0);

        // Assert
        Assert.True(estimates.TryGetBounds(s_edge, out var min, out var max, out _));
        Assert.Equal(4_000UL, min);
        Assert.Null(max);
    }

    [Fact]
    public void Given_Clone_When_OriginalChanges_Then_CloneUnchanged()
    {
        // Arrange
        var estimates = new LiquidityEstimates();
        var clone = estimates.Clone();

        // Act
        estimates.RecordFailure(s_edge, 1, 0);

        // Assert
        Assert.Equal(1, estimates.Count);
        Assert.Equal(0, clone.Count);
        Assert.True(estimates.Remove(s_edge));
    }
}