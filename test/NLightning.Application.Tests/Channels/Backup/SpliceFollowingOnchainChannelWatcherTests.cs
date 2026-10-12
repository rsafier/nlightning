using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Channels.Backup;

using Application.Channels.Backup;
using Application.Channels.Backup.Interfaces;
using Application.Onchain.Interfaces;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;

public class SpliceFollowingOnchainChannelWatcherTests
{
    private static readonly OutpointSpentEventArgs s_spend =
        new(new ChannelId(new byte[32]), new SignedTransaction(new TxId(new byte[32]), [0x02]), 600, 1,
            new TxId(Enumerable.Repeat((byte)1, 32).ToArray()), 0, new Hash(new byte[32]));

    [Fact]
    public async Task Given_TheRestoreServiceTakesTheSpend_When_Handled_Then_TheWatcherDoesNotSeeIt()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var inner = new Mock<IOnchainChannelWatcher>();
        var restore = new Mock<IChannelRestoreService>();
        restore.Setup(r => r.TryHandleRecoveryFundingSpendAsync(s_spend, It.IsAny<CancellationToken>()))
               .ReturnsAsync(true);
        var watcher = new SpliceFollowingOnchainChannelWatcher(() => inner.Object, () => restore.Object);

        // Act
        var outcome = await watcher.HandleFundingSpentAsync(s_spend, ct);

        // Assert
        Assert.Null(outcome);
        inner.Verify(w => w.HandleFundingSpentAsync(It.IsAny<OutpointSpentEventArgs>(), It.IsAny<CancellationToken>()),
                     Times.Never);
    }

    [Fact]
    public async Task Given_AnyOtherSpend_When_Handled_Then_TheWatcherGetsIt()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var expected = new FundingSpendOutcome(ChannelCloseKind.RemoteCommitment, 1, false);
        var inner = new Mock<IOnchainChannelWatcher>();
        inner.Setup(w => w.HandleFundingSpentAsync(s_spend, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var restore = new Mock<IChannelRestoreService>();
        restore.Setup(r => r.TryHandleRecoveryFundingSpendAsync(s_spend, It.IsAny<CancellationToken>()))
               .ReturnsAsync(false);
        var watcher = new SpliceFollowingOnchainChannelWatcher(() => inner.Object, () => restore.Object);

        // Act
        var outcome = await watcher.HandleFundingSpentAsync(s_spend, ct);

        // Assert
        Assert.Same(expected, outcome);
    }

    [Fact]
    public async Task Given_TheRestoreServiceFailsOnARecoverySpend_When_Handled_Then_ItIsNotRecordedAsAClose()
    {
        // Arrange: a failure is only possible once the spend is known to be a recovery channel's non-commitment spend
        var ct = TestContext.Current.CancellationToken;
        var inner = new Mock<IOnchainChannelWatcher>();
        var restore = new Mock<IChannelRestoreService>();
        restore.Setup(r => r.TryHandleRecoveryFundingSpendAsync(s_spend, It.IsAny<CancellationToken>()))
               .ThrowsAsync(new InvalidOperationException("database is locked"));
        var watcher = new SpliceFollowingOnchainChannelWatcher(() => inner.Object, () => restore.Object);

        // Act
        var outcome = await watcher.HandleFundingSpentAsync(s_spend, ct);

        // Assert
        Assert.Null(outcome);
        inner.Verify(w => w.HandleFundingSpentAsync(It.IsAny<OutpointSpentEventArgs>(), It.IsAny<CancellationToken>()),
                     Times.Never);
    }

    [Fact]
    public void Given_AWatcherRegisteredBefore_When_TheBackupServicesAreAdded_Then_ItIsWrappedOnce()
    {
        // Arrange
        var own = Mock.Of<IOnchainChannelWatcher>();
        var services = new ServiceCollection();
        services.AddSingleton(own);

        // Act
        services.AddChannelBackupServices();
        services.AddChannelBackupServices();
        using var provider = services.BuildServiceProvider();

        // Assert
        var wrapper = Assert.IsType<SpliceFollowingOnchainChannelWatcher>(
            provider.GetRequiredService<IOnchainChannelWatcher>());
        Assert.Same(own, wrapper.Inner);
        Assert.Single(services, d => d.ServiceType == typeof(IOnchainChannelWatcher));
    }
}