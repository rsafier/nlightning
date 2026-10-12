using Microsoft.Extensions.Logging;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Bitcoin.Wallet;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Options;

/// <summary>
/// The poll-only chain monitor (<see cref="ChainNotificationMode.Poll"/>, NL-1094): the poll is the block source and
/// hands every new tip to the ZMQ block path; the mempool is polled with <c>gettxspendingprevout</c>.
/// </summary>
public partial class BlockchainMonitorServiceTests
{
    private BlockchainMonitorService CreatePollService(FakeBitcoinChain chain,
                                                       ILogger<BlockchainMonitorService>? logger = null,
                                                       TimeSpan? pollInterval = null, bool? watchMempool = null) =>
        CreateService(chain, logger: logger, configure: o =>
        {
            o.Notifications = ChainNotificationMode.Poll;
            o.ZmqHost = null;
            o.ZmqBlockPort = 0;
            o.ZmqTxPort = 0;
            o.PollInterval = pollInterval ?? TimeSpan.FromHours(1); // the tests drive PollChainAsync themselves
            o.WatchMempool = watchMempool;
        });

    [Fact]
    public async Task Given_PollMode_When_SeveralBlocksAreMinedBetweenPolls_Then_OnePollProcessesThemInOrderWithoutAWarning()
    {
        // Arrange: no ZMQ endpoint at all
        var logger = new RecordingLogger();
        var service = CreatePollService(_chain, logger);
        await service.StartAsync(0, TestContext.Current.CancellationToken);
        var heights = new List<uint>();
        service.OnNewBlockDetected += (_, args) => heights.Add(args.Height);
        _chain.Mine();
        _chain.Mine();
        _chain.Mine();

        // Act
        var polled = await service.PollChainAsync();
        await service.StopAsync();

        // Assert: one unit of work per block, in order; a gap between polls is routine, so no warning
        Assert.True(polled);
        Assert.Equal([111u, 112u, 113u], heights);
        Assert.Equal(113u, service.LastProcessedBlockHeight);
        Assert.Equal(0, service.TipPollCatchUps);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Given_PollMode_When_TheTipIsAlreadyProcessed_Then_PollingAgainDoesNothing()
    {
        // Arrange
        var service = CreatePollService(_chain);
        await service.StartAsync(0, TestContext.Current.CancellationToken);
        _chain.Mine();
        await service.PollChainAsync();
        var heights = new List<uint>();
        service.OnNewBlockDetected += (_, args) => heights.Add(args.Height);
        var saves = _steps.Count(s => s == "save");

        // Act
        var second = await service.PollChainAsync();
        var third = await service.PollChainAsync();
        await service.StopAsync();

        // Assert: nothing processed twice, nothing saved
        Assert.False(second);
        Assert.False(third);
        Assert.Empty(heights);
        Assert.Equal(saves, _steps.Count(s => s == "save"));
        Assert.Equal(111u, service.LastProcessedBlockHeight);
    }

    [Fact]
    public async Task Given_PollMode_When_TheChainReorgsBetweenPolls_Then_TheDisconnectedBlocksAreRaisedBeforeTheNewBranch()
    {
        // Arrange: 111 and 112 processed, then 112 is replaced by a two-block branch
        _mockWatchedTransactionRepository.Setup(x => x.GetCompletedFirstSeenAboveAsync(It.IsAny<uint>()))
                                         .ReturnsAsync([]);
        var service = CreatePollService(_chain);
        await service.StartAsync(0, TestContext.Current.CancellationToken);
        _chain.Mine();
        _chain.Mine();
        await service.PollChainAsync();
        var events = new List<string>();
        service.OnBlockDisconnected += (_, args) => events.Add($"disconnected {args.Height}");
        service.OnNewBlockDetected += (_, args) => events.Add($"new {args.Height}");
        var newBranch = _chain.Reorg(111, 2);

        // Act
        var polled = await service.PollChainAsync();
        await service.StopAsync();

        // Assert
        Assert.True(polled);
        Assert.Equal(["disconnected 112", "new 112", "new 113"], events);
        Assert.Equal(113u, service.LastProcessedBlockHeight);
        Assert.False(service.IsChainProcessingHalted);
        Assert.Equal(newBranch[^1].GetHash(), _chain[113].GetHash());
    }

    [Fact]
    public async Task Given_PollMode_When_TheTipIsReplacedAtTheSameHeight_Then_ItIsRewoundAndReprocessed()
    {
        // Arrange: ZMQ would announce the competing block; the poll sees the same height with another hash
        _mockWatchedTransactionRepository.Setup(x => x.GetCompletedFirstSeenAboveAsync(It.IsAny<uint>()))
                                         .ReturnsAsync([]);
        var service = CreatePollService(_chain);
        await service.StartAsync(0, TestContext.Current.CancellationToken);
        _chain.Mine();
        await service.PollChainAsync();
        var events = new List<string>();
        service.OnBlockDisconnected += (_, args) => events.Add($"disconnected {args.Height}");
        service.OnNewBlockDetected += (_, args) => events.Add($"new {args.Height} {args.BlockHash}");
        var replacement = _chain.Reorg(110, 1)[0];

        // Act
        var polled = await service.PollChainAsync();
        await service.StopAsync();

        // Assert
        Assert.True(polled);
        Assert.Equal(["disconnected 111", $"new 111 {new Hash(replacement.GetHash().ToBytes())}"], events);
        Assert.Equal(111u, service.LastProcessedBlockHeight);
    }

    [Fact]
    public async Task Given_PollModeHalted_When_PolledWithoutANewTip_Then_ItWaitsForTheNextBlockAsZmqDoes()
    {
        // Arrange: block 100 (re-queued on start) fails every attempt, so the start halts
        _mockBlockchainStateRepository.Setup(x => x.Update(It.IsAny<BlockchainState>()))
                                      .Throws(new InvalidOperationException("db down"));
        var chain = new FakeBitcoinChain(102);
        var service = CreatePollService(chain);
        service.MaxBlockProcessingAttempts = 1;
        await service.StartAsync(0, TestContext.Current.CancellationToken);
        Assert.True(service.IsChainProcessingHalted);

        // Act: the first poll retries once (the tip is new to the poll), the second has nothing new, the third has a
        // new block
        var first = await service.PollChainAsync();
        var second = await service.PollChainAsync();
        chain.Mine();
        var third = await service.PollChainAsync();
        await service.StopAsync();

        // Assert: one attempt per new tip, besides the start's
        Assert.True(first);
        Assert.False(second);
        Assert.True(third);
        _mockBlockchainStateRepository.Verify(x => x.Update(It.IsAny<BlockchainState>()), Times.Exactly(3));
    }

    [Fact]
    public async Task Given_PollModeWithAShortInterval_When_ABlockIsMined_Then_TheLoopProcessesItWithoutZmq()
    {
        // Arrange
        var service = CreatePollService(_chain, pollInterval: TimeSpan.FromMilliseconds(50));
        await service.StartAsync(0, TestContext.Current.CancellationToken);
        _chain.Mine();
        _chain.Mine();

        // Act
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (service.LastProcessedBlockHeight < 112 && DateTime.UtcNow < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        await service.StopAsync();

        // Assert: caught up by the poll, which is not counted as a lost ZMQ notification
        Assert.Equal(112u, service.LastProcessedBlockHeight);
        Assert.Equal(0, service.TipPollCatchUps);
    }

    [Fact]
    public async Task Given_MempoolPoll_When_AWatchedOutpointAndThenItsSpenderAreSpent_Then_EachIsRaisedOnce()
    {
        // Arrange (O8 without ZMQ): a commitment spending the funding output, then an HTLC-success spending it
        var service = CreatePollService(_chain, watchMempool: true);
        var channelId = new ChannelId(Enumerable.Repeat((byte)0x0e, 32).ToArray());
        var fundingTxId = new TxId(Enumerable.Repeat((byte)0x0f, 32).ToArray());
        service.WatchOutpointSpend(channelId, fundingTxId, 1);
        var mempool = new List<MempoolSpendEventArgs>();
        service.OnWatchedOutpointSpentInMempool += (_, args) => mempool.Add(args);
        var commitment = CreateSpend(fundingTxId, 1);
        _chain.Mempool.Add(commitment);

        // Act
        var first = await service.PollMempoolAsync();
        var again = await service.PollMempoolAsync();
        var htlcSuccess = CreateSpend(commitment, 0);
        _chain.Mempool.Add(htlcSuccess);
        var child = await service.PollMempoolAsync();

        // Assert
        Assert.Equal(1, first);
        Assert.Equal(0, again);
        Assert.Equal(1, child);
        Assert.Equal(2, mempool.Count);
        Assert.Equal(new TxId(commitment.GetHash().ToBytes()), mempool[0].SpendingTransaction.TxId);
        Assert.False(mempool[0].SpendsUnconfirmedParent);
        Assert.Equal(new TxId(htlcSuccess.GetHash().ToBytes()), mempool[1].SpendingTransaction.TxId);
        Assert.True(mempool[1].SpendsUnconfirmedParent);
        Assert.Equal(channelId, mempool[1].ChannelId);
    }

    [Fact]
    public async Task Given_ANodeWithoutGetTxSpendingPrevOut_When_TheMempoolIsPolled_Then_NothingIsRaised()
    {
        // Arrange
        _chain.HasMempoolSpenders = false;
        var service = CreatePollService(_chain, watchMempool: true);
        var fundingTxId = new TxId(Enumerable.Repeat((byte)0x0f, 32).ToArray());
        service.WatchOutpointSpend(new ChannelId(new byte[32]), fundingTxId, 0);
        _chain.Mempool.Add(CreateSpend(fundingTxId, 0));

        // Act
        var raised = await service.PollMempoolAsync();

        // Assert
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task Given_PollModeWithoutMempoolWatch_When_TheLoopRuns_Then_TheMempoolIsNeverAsked()
    {
        // Arrange: unset WatchMempool means off with polling
        var service = CreatePollService(_chain, pollInterval: TimeSpan.FromMilliseconds(20));
        service.WatchOutpointSpend(new ChannelId(new byte[32]), new TxId(Enumerable.Repeat((byte)0x0f, 32).ToArray()),
                                   0);
        await service.StartAsync(0, TestContext.Current.CancellationToken);
        _chain.Mine();

        // Act
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (service.LastProcessedBlockHeight < 111 && DateTime.UtcNow < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        await service.StopAsync();

        // Assert
        Assert.Equal(111u, service.LastProcessedBlockHeight);
        Assert.Equal(0, _chain.MempoolSpenderQueries);
    }
}