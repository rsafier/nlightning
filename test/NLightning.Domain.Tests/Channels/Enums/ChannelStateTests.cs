namespace NLightning.Domain.Tests.Channels.Enums;

using Domain.Channels.Enums;

public class ChannelStateTests
{
    [Fact]
    public void Given_Failed_When_Compared_Then_SitsAfterOpenAndClosingAndBeforeClosed()
    {
        // ChannelModel.UpdateState only moves forward, so Open/Closing -> Failed -> Closed must be increasing
        // (BOLT2 plan §3.8, D11).

        // Assert
        Assert.Equal(35, (byte)ChannelState.Failed);
        Assert.True(ChannelState.Open < ChannelState.Failed);
        Assert.True(ChannelState.Closing < ChannelState.Failed);
        Assert.True(ChannelState.Failed < ChannelState.Closed);
    }

    [Fact]
    public void Given_OnchainResolving_When_Compared_Then_SitsAfterFailedAndBeforeClosed()
    {
        // A failed channel whose commitment confirms moves on to OnchainResolving, then to Closed (BOLT 5 plan §3.9)

        // Assert
        Assert.Equal(37, (byte)ChannelState.OnchainResolving);
        Assert.True(ChannelState.Closing < ChannelState.OnchainResolving);
        Assert.True(ChannelState.Failed < ChannelState.OnchainResolving);
        Assert.True(ChannelState.OnchainResolving < ChannelState.Closed);
    }

    [Fact]
    public void Given_ExistingStates_When_Read_Then_PersistedValuesUnchanged()
    {
        // Assert (the values are stored as bytes; renumbering would corrupt existing rows)
        Assert.Equal(0, (byte)ChannelState.None);
        Assert.Equal(22, (byte)ChannelState.Open);
        Assert.Equal(30, (byte)ChannelState.Closing);
        Assert.Equal(40, (byte)ChannelState.Closed);
        Assert.Equal(50, (byte)ChannelState.Stale);
    }
}