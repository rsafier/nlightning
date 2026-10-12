namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;

public partial class BlockchainMonitorServiceTests
{
    [Theory]
    [InlineData(false, 112u, false)] // Cancelled before the target.
    [InlineData(false, 111u, true)]  // Completed history must repair its disconnected final block.
    [InlineData(true, 112u, true)]   // An active bounded job continues from the fork.
    public async Task Given_HistoryCheckpointOnDisconnectedBranch_When_MonitorRewinds_Then_CancellationIsPreservedAndCompletedHistoryRepairs(
        bool wasActive, uint targetHeight, bool expectedActive)
    {
        // Arrange: real monitor rewind over the fake chain, with the history repository in its shared save.
        var ct = TestContext.Current.CancellationToken;
        var history = new Mock<IWalletTransactionDbRepository>();
        _mockWatchedTransactionRepository.Setup(repository => repository.GetCompletedFirstSeenAboveAsync(It.IsAny<uint>()))
            .ReturnsAsync([]);
        history.Setup(repository => repository.GetUnconfirmedAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _mockUnitOfWork.Setup(work => work.WalletTransactionDbRepository).Returns(history.Object);
        await _service.StartAsync(0, ct);
        await _service.ProcessNewBlockAsync(_chain.Mine(), 111);
        var job = new WalletHistoryRescanState(Guid.NewGuid(), 100, 100, targetHeight,
            111, (await _chain.GetBlockHashAsync(111)).ToBytes(), 30, wasActive, false);
        history.Setup(repository => repository.GetRescanStateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(job);
        WalletHistoryRescanState? staged = null;
        history.Setup(repository => repository.UnconfirmAboveAsync(110))
            .Callback(() => _steps.Add("history-unconfirm")).ReturnsAsync(1);
        history.Setup(repository => repository.StageRescanStateAsync(It.IsAny<WalletHistoryRescanState>(), It.IsAny<CancellationToken>()))
            .Callback<WalletHistoryRescanState, CancellationToken>((state, _) =>
            {
                staged = state;
                _steps.Add("history-checkpoint");
            }).Returns(Task.CompletedTask);
        _steps.Clear();

        // Act: block 111 disconnects; delivering the replacement tip exercises the monitor's actual rewind path.
        var replacement = _chain.Reorg(110, 2);
        await _service.ProcessNewBlockAsync(replacement[^1], 112);

        // Assert: rewind history and its bounded job together, without reviving a cancelled incomplete job.
        history.Verify(repository => repository.UnconfirmAboveAsync(110), Times.Once);
        Assert.NotNull(staged);
        Assert.Equal(job.Generation, staged.Generation);
        Assert.Equal(110u, staged.CursorHeight);
        Assert.Null(staged.CursorHash);
        Assert.Equal(expectedActive, staged.IsActive);
        Assert.Equal(targetHeight, staged.TargetHeight);
        var unconfirm = _steps.IndexOf("history-unconfirm");
        var checkpoint = _steps.IndexOf("history-checkpoint");
        var save = _steps.FindIndex(checkpoint + 1, step => step == "save");
        Assert.True(unconfirm >= 0 && checkpoint > unconfirm && save > checkpoint);
        Assert.DoesNotContain("save", _steps.Skip(unconfirm + 1).Take(checkpoint - unconfirm - 1));
        await _service.StopAsync();
    }
}