namespace NLightning.Application.Tests.Node.Services;

using Application.Node.Services;

public class NodeDrainStateTests
{
    [Fact]
    public void Given_ANewState_When_Read_Then_NotDraining()
    {
        // Arrange
        var state = new NodeDrainState();

        // Act
        var draining = state.IsDraining;

        // Assert
        Assert.False(draining);
    }

    [Fact]
    public void Given_ADrainRunning_When_BegunAgain_Then_Refused()
    {
        // Arrange - NL-591: one shutdown at a time
        var state = new NodeDrainState();
        Assert.True(state.TryBeginDrain());

        // Act
        var second = state.TryBeginDrain();

        // Assert
        Assert.False(second);
        Assert.True(state.IsDraining);
    }

    [Fact]
    public void Given_AnEndedDrain_When_BegunAgain_Then_Draining()
    {
        // Arrange - a refused shutdown (HTLCs in flight) ends its drain; a later one may start
        var state = new NodeDrainState();
        state.TryBeginDrain();
        state.EndDrain();
        Assert.False(state.IsDraining);

        // Act
        var again = state.TryBeginDrain();

        // Assert
        Assert.True(again);
        Assert.True(state.IsDraining);
    }
}