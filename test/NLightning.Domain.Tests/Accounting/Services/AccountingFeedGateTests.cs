namespace NLightning.Domain.Tests.Accounting.Services;

using Domain.Accounting.Constants;
using Domain.Accounting.Services;

public class AccountingFeedGateTests
{
    private const string LiveKey = "invoice:00";

    [Fact]
    public void Given_ANewGate_When_AnEventIsOffered_Then_ItIsAdmitted()
    {
        // Arrange
        var gate = new AccountingFeedGate();

        // Act
        var admitted = gate.Admits(LiveKey);

        // Assert
        Assert.True(admitted);
        Assert.False(gate.IsHeld);
        Assert.Equal(0, gate.DroppedCount);
    }

    [Fact]
    public void Given_AHeldGate_When_EventsAreOffered_Then_OnlyTheCutoversOwnAreAdmittedAndTheRestCounted()
    {
        // Arrange
        var gate = new AccountingFeedGate();
        gate.Hold("the accounting cutover failed");

        // Act
        var live = gate.Admits(LiveKey);
        var marker = gate.Admits(AccountingEventKeys.Cutover());
        var opening = gate.Admits(AccountingEventKeys.OpeningBalance("wallet"));

        // Assert
        Assert.False(live);
        Assert.True(marker);
        Assert.True(opening);
        Assert.Equal(1, gate.DroppedCount);
        Assert.Equal("the accounting cutover failed", gate.Reason);
    }

    [Fact]
    public void Given_AHeldGate_When_Released_Then_LiveEventsAreAdmittedAgain()
    {
        // Arrange
        var gate = new AccountingFeedGate();
        gate.Hold("failed");

        // Act
        gate.Release();

        // Assert
        Assert.False(gate.IsHeld);
        Assert.Null(gate.Reason);
        Assert.True(gate.Admits(LiveKey));
    }
}