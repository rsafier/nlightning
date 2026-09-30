namespace NLightning.Infrastructure.Tests.Node.Services;

using Infrastructure.Node.Services;

public class PingRateLimiterTests
{
    private static DateTimeOffset At(int second) => new(2026, 1, 1, 0, 0, second, TimeSpan.Zero);

    [Fact]
    public void Given_PingsUpToTheLimit_When_Registered_Then_EachIsAllowed()
    {
        // Arrange
        var limiter = new PingRateLimiter();

        // Act / Assert
        for (var i = 0; i < PingRateLimiter.DefaultMaxAnsweredPings; i++)
            Assert.True(limiter.TryRegisterAnswer(At(0)));
    }

    [Fact]
    public void Given_TheLimitIsUsedUp_When_AnotherPingArrivesWithinTheInterval_Then_ItIsRefused()
    {
        // Arrange (NL-005, BOLT 1: limited precautions against ping flooding)
        var limiter = new PingRateLimiter();
        FillTheLimit(limiter, At(0));

        // Act / Assert - just before the window rolls over the ping is still refused
        Assert.False(limiter.TryRegisterAnswer(At(9)));
    }

    [Fact]
    public void Given_TheLimitIsUsedUp_When_AnotherPingArrivesAfterTheInterval_Then_ItIsAllowedAgain()
    {
        // Arrange
        var limiter = new PingRateLimiter();
        FillTheLimit(limiter, At(0));

        // Act / Assert - every answer from second 0 left the window, so a full new burst fits
        for (var i = 0; i < PingRateLimiter.DefaultMaxAnsweredPings; i++)
            Assert.True(limiter.TryRegisterAnswer(At(10)));
        Assert.False(limiter.TryRegisterAnswer(At(10)));
    }

    [Fact]
    public void Given_ScatteredAnswers_When_TheWindowMoves_Then_OnlyTheAnswersInsideItCount()
    {
        // Arrange
        var limiter = new PingRateLimiter();
        for (var second = 0; second < PingRateLimiter.DefaultMaxAnsweredPings; second++)
            Assert.True(limiter.TryRegisterAnswer(At(second)));

        // Act / Assert - at second 10 the answer from second 0 left the window, the others are still counted
        Assert.True(limiter.TryRegisterAnswer(At(10)));
        Assert.False(limiter.TryRegisterAnswer(At(10)));
        Assert.True(limiter.TryRegisterAnswer(At(14)));
    }

    [Fact]
    public void Given_ACustomLimit_When_ItIsUsedUp_Then_FurtherPingsAreRefusedUntilTheWindowRolls()
    {
        // Arrange
        var limiter = new PingRateLimiter(1, TimeSpan.FromSeconds(10));

        // Act / Assert
        Assert.True(limiter.TryRegisterAnswer(At(0)));
        Assert.False(limiter.TryRegisterAnswer(At(5)));
        Assert.True(limiter.TryRegisterAnswer(At(10)));
    }

    private static void FillTheLimit(PingRateLimiter limiter, DateTimeOffset now)
    {
        for (var i = 0; i < PingRateLimiter.DefaultMaxAnsweredPings; i++)
            Assert.True(limiter.TryRegisterAnswer(now));
    }
}