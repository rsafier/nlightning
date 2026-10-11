namespace NLightning.Testing.Cluster.Tests;

public class PollTests
{
    private static readonly TimeSpan s_tick = TimeSpan.FromMilliseconds(1);

    [Fact]
    public async Task Given_AConditionTrueOnTheThirdTry_When_Polled_Then_ItReturns()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var tries = 0;

        // Act
        await Poll.UntilAsync(_ => Task.FromResult(++tries == 3), TimeSpan.FromSeconds(5), s_tick, "three", ct);

        // Assert
        Assert.Equal(3, tries);
    }

    [Fact]
    public async Task Given_AConditionNeverTrue_When_Polled_Then_TheTimeoutSaysWhatWasAwaited()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;

        // Act
        var e = await Assert.ThrowsAsync<TimeoutException>(
                    () => Poll.UntilAsync(_ => Task.FromResult(false), TimeSpan.FromMilliseconds(20), s_tick,
                                          "the impossible", ct));

        // Assert
        Assert.Contains("the impossible", e.Message);
    }

    [Fact]
    public async Task Given_ACheckThatReportsWhatIsMissing_When_ItTimesOut_Then_TheLastReportEndsTheMessage()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var tries = 0;

        // Act
        var e = await Assert.ThrowsAsync<TimeoutException>(
                    () => Poll.UntilDoneAsync(_ => Task.FromResult<string?>($"try {++tries}"),
                                              TimeSpan.FromMilliseconds(20), "done", ct, s_tick));

        // Assert
        Assert.Contains("done", e.Message);
        Assert.EndsWith($": try {tries}", e.Message);
    }

    [Fact]
    public async Task Given_ACheckThatIsDone_When_Polled_Then_ItReturnsAtOnce()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var tries = 0;

        // Act
        await Poll.UntilDoneAsync(_ =>
        {
            tries++;
            return Task.FromResult<string?>(null);
        }, TimeSpan.FromSeconds(1), "done", ct);

        // Assert
        Assert.Equal(1, tries);
    }

    [Fact]
    public async Task Given_AProbeThatAnswersLater_When_Polled_Then_ItsValueIsReturned()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var tries = 0;

        // Act
        var value = await Poll.ForAsync(_ => Task.FromResult(++tries < 2 ? null : "value"), TimeSpan.FromSeconds(5),
                                        s_tick, "a value", ct);

        // Assert
        Assert.Equal("value", value);
    }

    [Fact]
    public async Task Given_AProbeThatNeverAnswers_When_Polled_Then_TheTimeoutCarriesTheLastState()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;

        // Act
        var e = await Assert.ThrowsAsync<TimeoutException>(
                    () => Poll.ForAsync(_ => Task.FromResult<string?>(null), TimeSpan.FromMilliseconds(20), s_tick,
                                        "a value", ct, () => "still nothing"));

        // Assert
        Assert.EndsWith("(still nothing)", e.Message);
    }
}