using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Onchain;

using Application.Onchain;
using Application.Onchain.Interfaces;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;

/// <summary>
/// Splicing plan §3.6, SP2-C-T1: the channel manager hands a spend of any watched outpoint that is not the channel's
/// current funding output to the executor; the executor passes a spend of a <b>funding</b> outpoint (a pending splice's,
/// watched from our splice <c>commitment_signed</c> on, or a retired one) to the on-chain watcher, which classifies it
/// against that funding, and keeps resolution outputs for itself.
/// </summary>
public sealed class OnchainSpliceExecutorTests : IDisposable
{
    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0x3C, 32).ToArray());
    private static readonly TxId s_spliceTxId = new(Enumerable.Repeat((byte)0xC5, 32).ToArray());
    private static readonly TxId s_commitmentTxId = new(Enumerable.Repeat((byte)0xC6, 32).ToArray());

    private readonly Mock<IWatchedOutpointDbRepository> _watches = new();
    private readonly Mock<IOnchainChannelWatcher> _watcher = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly ServiceProvider _provider;

    public OnchainSpliceExecutorTests()
    {
        _unitOfWork.SetupGet(u => u.WatchedOutpointDbRepository).Returns(_watches.Object);
        var services = new ServiceCollection();
        services.AddScoped(_ => _unitOfWork.Object);
        services.AddSingleton(_watcher.Object);
        _provider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task Given_ASpendOfAPendingSpliceFundingOutpoint_When_Handed_Then_TheWatcherClassifiesIt()
    {
        // Arrange
        _watches.Setup(w => w.GetAsync(s_spliceTxId, 1))
                .ReturnsAsync(new WatchedOutpointModel(s_spliceTxId, 1, s_channelId,
                                                       WatchedOutpointPurpose.FundingOutput));
        var args = SpentBy(s_spliceTxId, 1);

        // Act
        await CreateExecutor().HandleOutputSpentAsync(args, TestContext.Current.CancellationToken);

        // Assert
        _watcher.Verify(w => w.HandleFundingSpentAsync(args, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_ASpendOfAResolutionOutput_When_Handed_Then_TheWatcherIsNotAsked()
    {
        // Arrange
        _watches.Setup(w => w.GetAsync(s_commitmentTxId, 0))
                .ReturnsAsync(new WatchedOutpointModel(s_commitmentTxId, 0, s_channelId,
                                                       WatchedOutpointPurpose.ResolutionOutput));

        // Act
        await CreateExecutor().HandleOutputSpentAsync(SpentBy(s_commitmentTxId, 0),
                                                      TestContext.Current.CancellationToken);

        // Assert
        _watcher.Verify(w => w.HandleFundingSpentAsync(It.IsAny<OutpointSpentEventArgs>(),
                                                       It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Given_ALockedSplice_When_ItLeavesTheChainAndComesBack_Then_ReportedWhileTheOldFundingIsUnspent()
    {
        // Arrange (SP2-C-T4): the splice replaced the initial funding; the old funding's watch records the splice
        using var pair = new Channels.Services.RealSigningCommitmentPair(false);
        var channel = pair.Alice.Channel;
        var old = Domain.Channels.Splicing.ChannelFunding.FromFundingOutput(channel.FundingOutput!)! with
        {
            FundingTxId = s_commitmentTxId,
            Status = Domain.Channels.Splicing.Enums.ChannelFundingStatus.Replaced
        };
        var current = Domain.Channels.Splicing.ChannelFunding.FromFundingOutput(channel.FundingOutput!)!;
        var fundings = new Mock<IChannelFundingDbRepository>();
        fundings.Setup(f => f.GetByChannelIdAsync(channel.ChannelId)).ReturnsAsync([old, current]);
        _unitOfWork.SetupGet(u => u.ChannelFundingDbRepository).Returns(fundings.Object);
        var oldWatch = new WatchedOutpointModel(old.FundingTxId, old.OutputIndex, channel.ChannelId,
                                                WatchedOutpointPurpose.FundingOutput);
        oldWatch.MarkSpent(s_spliceTxId, 650, new Domain.Crypto.ValueObjects.Hash(new byte[32]));
        _watches.Setup(w => w.GetAsync(old.FundingTxId, old.OutputIndex)).ReturnsAsync(oldWatch);
        var memory = new Mock<IChannelMemoryRepository>();
        memory.Setup(m => m.FindChannels(It.IsAny<Func<Domain.Channels.Models.ChannelModel, bool>>()))
              .Returns((Func<Domain.Channels.Models.ChannelModel, bool> predicate) =>
                           new[] { channel }.Where(predicate).ToList());
        var executor = CreateExecutor(memory.Object);
        await executor.RunRoundAsync(700, TestContext.Current.CancellationToken);
        Assert.False(executor.SpliceReorgs.IsSpliceReorgedOut(channel.ChannelId, old.FundingTxId));

        // Act: the splice's block is disconnected (the monitor clears the recorded spend)
        oldWatch.ClearSpend();
        await executor.RunRoundAsync(701, TestContext.Current.CancellationToken);
        var reorged = executor.SpliceReorgs.IsSpliceReorgedOut(channel.ChannelId, old.FundingTxId);
        oldWatch.MarkSpent(s_spliceTxId, 702, new Domain.Crypto.ValueObjects.Hash(new byte[32]));
        await executor.RunRoundAsync(702, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(reorged);
        Assert.False(executor.SpliceReorgs.IsSpliceReorgedOut(channel.ChannelId, old.FundingTxId));
    }

    [Fact]
    public async Task Given_AReplacedFundingWithoutAWatch_When_Checked_Then_ReportedAsUnwatched()
    {
        // Arrange (SP2-C-T4): the replaced funding's watch row is gone (removed at the lock by an older build)
        using var pair = new Channels.Services.RealSigningCommitmentPair(false);
        var channel = pair.Alice.Channel;
        var old = Domain.Channels.Splicing.ChannelFunding.FromFundingOutput(channel.FundingOutput!)! with
        {
            FundingTxId = s_commitmentTxId,
            Status = Domain.Channels.Splicing.Enums.ChannelFundingStatus.Replaced
        };
        var current = Domain.Channels.Splicing.ChannelFunding.FromFundingOutput(channel.FundingOutput!)!;
        var fundings = new Mock<IChannelFundingDbRepository>();
        fundings.Setup(f => f.GetByChannelIdAsync(channel.ChannelId)).ReturnsAsync([old, current]);
        _unitOfWork.SetupGet(u => u.ChannelFundingDbRepository).Returns(fundings.Object);
        var memory = new Mock<IChannelMemoryRepository>();
        memory.Setup(m => m.FindChannels(It.IsAny<Func<Domain.Channels.Models.ChannelModel, bool>>()))
              .Returns((Func<Domain.Channels.Models.ChannelModel, bool> predicate) =>
                           new[] { channel }.Where(predicate).ToList());
        var executor = CreateExecutor(memory.Object);

        // Act
        await executor.RunRoundAsync(700, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(executor.SpliceReorgs.IsUnwatched(channel.ChannelId, old.FundingTxId));
        Assert.False(executor.SpliceReorgs.IsSpliceReorgedOut(channel.ChannelId, old.FundingTxId));
    }

    public void Dispose() => _provider.Dispose();

    private OnchainResolutionExecutor CreateExecutor(IChannelMemoryRepository? memory = null) =>
        new(new Mock<IChainBroadcaster>().Object, new Application.Channels.Services.ChannelLockProvider(),
            memory ?? new Mock<IChannelMemoryRepository>().Object, NullLogger<OnchainResolutionExecutor>.Instance,
            new Mock<IOutpointWatcher>().Object, _provider.GetRequiredService<IServiceScopeFactory>());

    private static OutpointSpentEventArgs SpentBy(TxId txId, uint vout) =>
        new(s_channelId, new SignedTransaction(s_commitmentTxId, [0x02, 0x00]), 700, 1, txId, vout);
}