namespace NLightning.Integration.Tests.Docker.Utils;

public class PollTests
{
    private static readonly TimeSpan s_interval = TimeSpan.FromMilliseconds(10);

    [Fact]
    public async Task Given_ConditionThatTurnsFalse_When_CheckingItHolds_Then_ReturnsFalse()
    {
        // Arrange: true on the first check only, like a connection torn down right after both ends listed it
        var checks = 0;

        // Act
        var holds = await Poll.HoldsAsync(() => ++checks == 1, TimeSpan.FromSeconds(5),
                                          TestContext.Current.CancellationToken, s_interval);

        // Assert
        Assert.False(holds);
        Assert.Equal(2, checks);
    }

    [Fact]
    public async Task Given_ConditionThatStaysTrue_When_CheckingItHolds_Then_ReturnsTrueAfterTheWindow()
    {
        // Arrange
        var window = TimeSpan.FromMilliseconds(100);
        var start = DateTime.UtcNow;

        // Act
        var holds = await Poll.HoldsAsync(() => true, window, TestContext.Current.CancellationToken, s_interval);

        // Assert
        Assert.True(holds);
        Assert.True(DateTime.UtcNow - start >= window);
    }
}