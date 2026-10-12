namespace NLightning.Domain.Tests.Channels.ValueObjects;

using Domain.Channels.ValueObjects;

public class ChannelSignerGuardTests
{
    [Fact]
    public void Given_TwoGuards_When_Merged_Then_EveryFieldTakesItsSafeDirection()
    {
        // Arrange
        var first = new ChannelSignerGuard(4, 3, 10, 8);
        var second = new ChannelSignerGuard(7, 2, 9, 6, true);

        // Act
        var merged = first.Merge(second);

        // Assert: higher local, revoked and remote numbers, the lower broadcast mark, data loss is sticky
        Assert.Equal(new ChannelSignerGuard(7, 3, 10, 6, true), merged);
        Assert.Equal(merged, second.Merge(first));
    }

    [Fact]
    public void Given_ARevokedNumber_When_Merged_Then_TheLocalNumberIsPastIt()
    {
        // Arrange: a released secret means the local commitment moved past it
        var guard = new ChannelSignerGuard(0, 5);

        // Act
        var merged = guard.Merge(new ChannelSignerGuard(0));

        // Assert
        Assert.Equal(6UL, merged.LocalCommitmentNumber);
    }

    [Fact]
    public void Given_AGuard_When_AskedWhetherItCoversAnother_Then_OnlyAWeakerOneIsCovered()
    {
        // Arrange
        var guard = new ChannelSignerGuard(6, 5, 7, 4);

        // Act & Assert
        Assert.True(guard.Covers(new ChannelSignerGuard(6, 5)));
        Assert.True(guard.Covers(new ChannelSignerGuard(3, null, 7, 9)));
        Assert.False(guard.Covers(new ChannelSignerGuard(6, null, 8)));
        Assert.False(guard.Covers(new ChannelSignerGuard(6, BroadcastSignedCommitmentNumber: 3)));
        Assert.False(guard.Covers(new ChannelSignerGuard(0, DataLossDetected: true)));
    }
}