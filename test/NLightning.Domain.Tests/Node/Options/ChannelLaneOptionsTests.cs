namespace NLightning.Domain.Tests.Node.Options;

using Domain.Node.Options;

public class ChannelLaneOptionsTests
{
    [Theory]
    [InlineData("Postgres", true)]
    [InlineData("postgresql", true)]
    [InlineData(" POSTGRES ", true)]
    [InlineData("Sqlite", false)]
    [InlineData("SqlServer", false)]
    [InlineData(null, false)]
    public void Given_ADatabaseProvider_When_TheDefaultIsAsked_Then_OnlyPostgresTurnsLanesOn(string? provider,
                                                                                              bool expected)
    {
        // Act
        var enabled = ChannelLaneOptions.DefaultFor(provider);

        // Assert (SQLite keeps the serial loop: one writer, owner decision 2026-10-11)
        Assert.Equal(expected, enabled);
    }

    [Fact]
    public void Given_UnsetOptions_When_Read_Then_LanesAreOffWithTheDefaultCaps()
    {
        // Arrange
        var options = new ChannelLaneOptions();

        // Act / Assert
        Assert.False(options.IsEnabled);
        Assert.Equal(8, options.MaxConcurrentPerPeer);
        Assert.Equal(32, options.MaxConcurrent);
        Assert.Empty(options.GetValidationErrors());
    }

    [Theory]
    [InlineData(0, 32)]
    [InlineData(257, 32)]
    [InlineData(8, 0)]
    [InlineData(8, 1_025)]
    public void Given_ACapOutOfRange_When_Validated_Then_ItIsAnError(int perPeer, int total)
    {
        // Arrange
        var options = new NodeOptions
        {
            ChannelLanes = new ChannelLaneOptions { MaxConcurrentPerPeer = perPeer, MaxConcurrent = total }
        };

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Single(errors, e => e.StartsWith("ChannelLanes:", StringComparison.Ordinal));
    }
}