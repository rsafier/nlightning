namespace NLightning.Application.Tests.Gossip.Sync;

using Application.Gossip.Sync;
using Application.Gossip.Sync.Interfaces;
using Domain.Channels.ValueObjects;

/// <summary>
/// BOLT 7 plan G3-T5: the payment retry path's gossip refresh reaches the sync manager's one-SCID query, without
/// blocking the caller and with one query per channel in flight.
/// </summary>
public class GossipSyncScidRefresherTests
{
    private static readonly ShortChannelId s_scid = new(500, 1, 0);

    [Fact]
    public async Task Given_ARefreshRequest_When_Requested_Then_TheSyncManagerQueriesTheScid()
    {
        // Arrange
        var queried = new TaskCompletionSource<ShortChannelId>(TaskCreationOptions.RunContinuationsAsynchronously);
        var syncManager = new Mock<IGossipSyncManager>();
        syncManager.Setup(m => m.QueryScidAsync(It.IsAny<ShortChannelId>(), It.IsAny<CancellationToken>()))
                   .Callback<ShortChannelId, CancellationToken>((scid, _) => queried.TrySetResult(scid))
                   .ReturnsAsync(true);
        var refresher = new GossipSyncScidRefresher(syncManager.Object);

        // Act
        var accepted = refresher.RequestRefresh(s_scid);

        // Assert
        Assert.True(accepted);
        Assert.Equal(s_scid, await queried.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_AQueryInFlight_When_TheSameScidIsRequestedAgain_Then_ItIsCoalescedUntilTheQueryEnds()
    {
        // Arrange
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var syncManager = new Mock<IGossipSyncManager>();
        syncManager.Setup(m => m.QueryScidAsync(s_scid, It.IsAny<CancellationToken>())).Returns(answer.Task);
        var refresher = new GossipSyncScidRefresher(syncManager.Object);
        Assert.True(refresher.RequestRefresh(s_scid));

        // Act
        var second = refresher.RequestRefresh(s_scid);
        answer.SetResult(false);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (refresher.InFlightCount > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        var third = refresher.RequestRefresh(s_scid);

        // Assert
        Assert.False(second);
        Assert.True(third);
    }

    [Fact]
    public async Task Given_TheQueryThrows_When_Requested_Then_TheCallerIsUnaffectedAndTheScidIsReleased()
    {
        // Arrange
        var syncManager = new Mock<IGossipSyncManager>();
        syncManager.Setup(m => m.QueryScidAsync(s_scid, It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new InvalidOperationException("boom"));
        var refresher = new GossipSyncScidRefresher(syncManager.Object);

        // Act
        var accepted = refresher.RequestRefresh(s_scid);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (refresher.InFlightCount > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(accepted);
        Assert.Equal(0, refresher.InFlightCount);
    }
}