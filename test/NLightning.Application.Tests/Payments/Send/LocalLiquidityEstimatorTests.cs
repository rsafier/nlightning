namespace NLightning.Application.Tests.Payments.Send;

using Application.Payments.Send;
using Channels.Fees;

public class LocalLiquidityEstimatorTests
{
    private const uint CltvExpiry = 1_144;

    [Fact]
    public void Given_ASnapshot_When_AskedAgain_Then_TheSameAnswerAsAFreshSearch()
    {
        // Arrange (NL-1360): the answer without planned HTLCs is kept per snapshot instance
        var snapshot = FeeTestKit.Create(600_000, 400_000, 2_500);
        var twin = FeeTestKit.Create(600_000, 400_000, 2_500);

        // Act
        var first = LocalLiquidityEstimator.MaxSendableMsat(snapshot, [], CltvExpiry);
        var again = LocalLiquidityEstimator.MaxSendableMsat(snapshot, [], CltvExpiry);
        var fresh = LocalLiquidityEstimator.MaxSendableMsat(twin, [], CltvExpiry);

        // Assert: positive, stable, and what an uncached search of an equal snapshot finds
        Assert.True(first > 0);
        Assert.Equal(first, again);
        Assert.Equal(fresh, again);
        Assert.True(first <= 600_000 * FeeTestKit.Sat);
    }

    [Fact]
    public void Given_AnHtlcPlanned_When_Asked_Then_TheKeptAnswerIsNotUsed()
    {
        // Arrange: the unplanned answer is kept first
        var snapshot = FeeTestKit.Create(600_000, 400_000, 2_500);
        var unplanned = LocalLiquidityEstimator.MaxSendableMsat(snapshot, [], CltvExpiry);

        // Act: with an HTLC planned before it, less is left
        var planned = LocalLiquidityEstimator.MaxSendableMsat(snapshot, [100_000 * FeeTestKit.Sat], CltvExpiry);

        // Assert
        Assert.True(planned < unplanned);
        Assert.Equal(unplanned, LocalLiquidityEstimator.MaxSendableMsat(snapshot, [], CltvExpiry));
    }

    [Fact]
    public void Given_AnotherSnapshotOfTheChannel_When_Asked_Then_ItIsSearchedAgain()
    {
        // Arrange: a payment moved the balance (the engine's next snapshot is another instance)
        var before = FeeTestKit.Create(600_000, 400_000, 2_500);
        var after = FeeTestKit.Create(300_000, 700_000, 2_500);
        var sendableBefore = LocalLiquidityEstimator.MaxSendableMsat(before, [], CltvExpiry);

        // Act
        var sendableAfter = LocalLiquidityEstimator.MaxSendableMsat(after, [], CltvExpiry);

        // Assert
        Assert.True(sendableAfter < sendableBefore);
        Assert.True(sendableAfter <= 300_000 * FeeTestKit.Sat);
    }
}