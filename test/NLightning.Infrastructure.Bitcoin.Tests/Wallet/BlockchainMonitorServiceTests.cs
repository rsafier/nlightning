using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Bitcoin.Wallet;
using Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Options;

/// <summary>
/// Unit tests of the chain monitor over a <see cref="FakeBitcoinChain"/> and a mocked unit of work. The persistence
/// semantics (one save per block, reorg rollback, rebroadcast after a restart) are proven on SQLite in
/// <c>Integration.Tests/Persistence/ChainMonitorPersistenceTests</c>.
/// </summary>
public class BlockchainMonitorServiceTests
{
    private readonly FakeBitcoinChain _chain = new(110);
    private readonly FakeServiceProvider _fakeServiceProvider;
    private readonly Mock<IUnitOfWork> _mockUnitOfWork;
    private readonly Mock<IBlockchainStateDbRepository> _mockBlockchainStateRepository;
    private readonly Mock<IWatchedTransactionDbRepository> _mockWatchedTransactionRepository;
    private readonly Mock<IWalletAddressesDbRepository> _mockWalletAddressesDbRepository;
    private readonly Mock<IUtxoDbRepository> _mockUtxoDbRepository;
    private readonly Mock<IWatchedOutpointDbRepository> _mockWatchedOutpointRepository;
    private readonly Mock<IBroadcastTransactionDbRepository> _mockBroadcastRepository;
    private readonly Mock<IBlockHeaderDbRepository> _mockBlockHeaderRepository;
    private readonly List<string> _steps = [];

    private readonly BlockchainMonitorService _service;

    public BlockchainMonitorServiceTests()
    {
        _mockUnitOfWork = new Mock<IUnitOfWork>();
        _fakeServiceProvider = new FakeServiceProvider();
        _fakeServiceProvider.AddService(typeof(IUnitOfWork), _mockUnitOfWork.Object);
        _mockBlockchainStateRepository = new Mock<IBlockchainStateDbRepository>();
        _mockWatchedTransactionRepository = new Mock<IWatchedTransactionDbRepository>();
        _mockWalletAddressesDbRepository = new Mock<IWalletAddressesDbRepository>();
        _mockUtxoDbRepository = new Mock<IUtxoDbRepository>();
        _mockWatchedOutpointRepository = new Mock<IWatchedOutpointDbRepository>();
        _mockBroadcastRepository = new Mock<IBroadcastTransactionDbRepository>();
        _mockBlockHeaderRepository = new Mock<IBlockHeaderDbRepository>();

        // Set up unit of work to return repositories
        _mockUnitOfWork.Setup(x => x.BlockchainStateDbRepository).Returns(_mockBlockchainStateRepository.Object);
        _mockUnitOfWork.Setup(x => x.WatchedTransactionDbRepository).Returns(_mockWatchedTransactionRepository.Object);
        _mockUnitOfWork.Setup(x => x.WalletAddressesDbRepository).Returns(_mockWalletAddressesDbRepository.Object);
        _mockUnitOfWork.Setup(x => x.UtxoDbRepository).Returns(_mockUtxoDbRepository.Object);
        _mockUnitOfWork.Setup(x => x.WatchedOutpointDbRepository).Returns(_mockWatchedOutpointRepository.Object);
        _mockUnitOfWork.Setup(x => x.BroadcastTransactionDbRepository).Returns(_mockBroadcastRepository.Object);
        _mockUnitOfWork.Setup(x => x.BlockHeaderDbRepository).Returns(_mockBlockHeaderRepository.Object);
        _mockUnitOfWork.Setup(x => x.SaveChangesAsync()).Callback(() => _steps.Add("save")).Returns(Task.CompletedTask);

        _mockWatchedTransactionRepository.Setup(x => x.GetAllPendingAsync()).ReturnsAsync([]);
        _mockWatchedOutpointRepository.Setup(x => x.AddMissingFundingOutpointsAsync()).ReturnsAsync([]);
        _mockWatchedOutpointRepository.Setup(x => x.GetActiveAsync()).ReturnsAsync([]);
        _mockBroadcastRepository.Setup(x => x.GetPendingAsync()).ReturnsAsync([]);
        _mockBlockHeaderRepository.Setup(x => x.GetAllAsync()).ReturnsAsync([]);
        _mockBlockchainStateRepository.Setup(x => x.GetStateAsync())
                                      .ReturnsAsync(new BlockchainState(100, Hash.Empty, DateTime.UtcNow));

        _service = CreateService(_chain);
    }

    [Fact]
    public async Task Given_ExistingState_When_Starting_Then_StateAndWatchesLoadedAndBlocksUpToTheTipProcessed()
    {
        // Arrange
        var heights = new List<uint>();
        _service.OnNewBlockDetected += (_, args) => heights.Add(args.Height);

        // Act
        await _service.StartAsync(0, TestContext.Current.CancellationToken);

        // Assert (NL-215: the tip, 110, is processed too)
        _mockBlockchainStateRepository.Verify(x => x.GetStateAsync(), Times.Once);
        _mockWatchedTransactionRepository.Verify(x => x.GetAllPendingAsync(), Times.Once);
        _mockWatchedOutpointRepository.Verify(x => x.AddMissingFundingOutpointsAsync(), Times.Once);
        _mockWatchedOutpointRepository.Verify(x => x.GetActiveAsync(), Times.Once);
        _mockBroadcastRepository.Verify(x => x.GetPendingAsync(), Times.Once);
        Assert.Equal(Enumerable.Range(100, 11).Select(h => (uint)h), heights);
        Assert.Equal(110u, _service.LastProcessedBlockHeight);
        _mockBlockchainStateRepository.Verify(x => x.Add(It.IsAny<BlockchainState>()), Times.Never);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(50u)]
    public async Task Given_NoState_When_Starting_Then_StateCreatedAtTheHeightOfBirthAndSavedBeforeAnyBlock(
        uint heightOfBirth)
    {
        // Arrange
        _mockBlockchainStateRepository.Setup(x => x.GetStateAsync()).ReturnsAsync((BlockchainState?)null);
        uint? created = null;
        _mockBlockchainStateRepository.Setup(x => x.Add(It.IsAny<BlockchainState>()))
                                      .Callback<BlockchainState>(s =>
                                       {
                                           created = s.LastProcessedHeight;
                                           _steps.Add("add state");
                                       });
        _mockBlockchainStateRepository.Setup(x => x.Update(It.IsAny<BlockchainState>()))
                                      .Callback<BlockchainState>(s => _steps.Add($"update {s.LastProcessedHeight}"));

        // Act
        await _service.StartAsync(heightOfBirth, TestContext.Current.CancellationToken);
        await _service.StopAsync();

        // Assert: the state exists (saved) before the first block updates it; every block from the height of birth on
        Assert.Equal(heightOfBirth, created);
        Assert.Equal(["add state", "save", $"update {heightOfBirth}", "save"], _steps.Take(4));
        Assert.Equal(110u, _service.LastProcessedBlockHeight);
    }

    [Fact]
    public async Task Given_WatchTransaction_When_Called_Then_AddedAndSaved()
    {
        // Arrange
        var channelId = new ChannelId(new byte[32]);
        var txId = new TxId(new byte[32]);

        // Act
        await _service.WatchTransactionAsync(channelId, txId, 6);

        // Assert
        _mockWatchedTransactionRepository.Verify(
            x => x.Add(It.Is<WatchedTransactionModel>(t => t.ChannelId.Equals(channelId) && t.TransactionId.Equals(txId)
                                                        && t.RequiredDepth == 6)), Times.Once);
        _mockUnitOfWork.Verify(x => x.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task Given_NewBlockAfterMissedOnes_When_Delivered_Then_EachIsProcessedInOrderWithItsOwnSave()
    {
        // Arrange
        await _service.StartAsync(0, TestContext.Current.CancellationToken);
        _chain.Mine();
        _chain.Mine();
        var block = _chain.Mine();
        var heights = new List<uint>();
        _service.OnNewBlockDetected += (_, args) => heights.Add(args.Height);
        var saves = _steps.Count(s => s == "save");

        // Act
        await _service.ProcessNewBlockAsync(block, 113);

        // Assert
        Assert.Equal([111u, 112u, 113u], heights);
        Assert.Equal(saves + 3, _steps.Count(s => s == "save"));
        _mockBlockHeaderRepository.Verify(x => x.AddOrReplaceAsync(It.Is<BlockHeaderModel>(h => h.Height == 113)),
                                          Times.Once);
    }

    [Fact]
    public async Task Given_WatchWithDepthOne_When_ItsBlockIsProcessed_Then_ConfirmedAfterTheSaveWithItsBlockIndex()
    {
        // Arrange
        await _service.StartAsync(0, TestContext.Current.CancellationToken);
        var unrelated = CreateTransaction(0x01);
        var watched = CreateTransaction(0x02);
        var channelId = new ChannelId(Enumerable.Repeat((byte)0x03, 32).ToArray());
        _service.TrackWatchedTransaction(new WatchedTransactionModel(channelId,
                                                                     new TxId(watched.GetHash().ToBytes()), 1));
        _mockWatchedTransactionRepository.Setup(x => x.Update(It.IsAny<WatchedTransactionModel>()))
                                         .Callback((WatchedTransactionModel w) =>
                                                       _steps.Add($"update watch {w.IsCompleted}"));
        TransactionConfirmedEventArgs? confirmed = null;
        _service.OnTransactionConfirmed += (_, args) =>
        {
            _steps.Add("confirmed");
            confirmed = args;
        };

        // Act
        await _service.ProcessNewBlockAsync(_chain.Mine(unrelated, watched), 111);

        // Assert: coinbase 0, unrelated 1, watched 2 (BOLT 7 short_channel_id index)
        Assert.NotNull(confirmed);
        Assert.Equal(111u, confirmed.Height);
        Assert.Equal(111u, confirmed.WatchedTransaction.FirstSeenAtHeight);
        Assert.Equal(2u, confirmed.WatchedTransaction.TransactionIndex);
        Assert.True(confirmed.WatchedTransaction.IsCompleted);
        var tail = _steps.SkipWhile(s => s != "update watch False").ToList();
        Assert.Equal(["update watch False", "update watch True", "save", "confirmed"], tail);
        _mockWatchedOutpointRepository.Verify(
            x => x.AddFundingOutpointIfMissingAsync(channelId, new TxId(watched.GetHash().ToBytes())), Times.Once);
    }

    [Fact]
    public async Task Given_BlockProcessingThrows_When_ProcessingQueue_Then_RoundHaltsAndLaterRoundResumes()
    {
        // Arrange
        var failing = true;
        var processedHeights = new List<uint>();
        _mockBlockchainStateRepository.Setup(x => x.Update(It.IsAny<BlockchainState>()))
                                      .Callback<BlockchainState>(s =>
                                       {
                                           if (failing)
                                               throw new InvalidOperationException("db down");

                                           processedHeights.Add(s.LastProcessedHeight);
                                       });
        var raised = 0;
        var chain = new FakeBitcoinChain(102);
        var service = CreateService(chain);
        service.OnNewBlockDetected += (_, _) => raised++;
        service.MaxBlockProcessingAttempts = 3;
        service.BlockRetryBaseDelay = TimeSpan.FromMilliseconds(1);

        // Act
        await service.StartAsync(0, TestContext.Current.CancellationToken)
                     .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert: block 100 (re-queued on start) was tried exactly MaxBlockProcessingAttempts times, 101 never, and
        // no event was raised for a block whose save failed
        _mockBlockchainStateRepository.Verify(x => x.Update(It.IsAny<BlockchainState>()), Times.Exactly(3));
        Assert.Equal(100u, service.LastProcessedBlockHeight);
        Assert.True(service.IsChainProcessingHalted);
        Assert.Empty(processedHeights);
        Assert.Equal(0, raised);

        // Act: the failure clears and a new block arrives
        failing = false;
        await service.ProcessNewBlockAsync(chain.Mine(), 103);
        await service.StopAsync();

        // Assert: the queue resumes from the failed block, in order, without skipping any
        Assert.Equal([100u, 101u, 102u, 103u], processedHeights);
        Assert.Equal(103u, service.LastProcessedBlockHeight);
        Assert.False(service.IsChainProcessingHalted);
        Assert.Equal(4, raised);
    }

    [Fact]
    public async Task Given_DepositInLastProcessedBlock_When_RestartingWithNewBlocks_Then_NewBlocksAreProcessed()
    {
        // Arrange
        var address = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        var walletAddress = new WalletAddressModel(AddressType.P2Wpkh, 0, false, address.ToString());

        var depositTx = Network.RegTest.CreateTransaction();
        depositTx.Outputs.Add(Money.Satoshis(50_000), address);
        var chain = new FakeBitcoinChain(99);
        chain.Mine(depositTx);
        chain.Mine();

        // The deposit in block 100 was already processed and saved before the restart
        var knownUtxo = new UtxoModel(new TxId(depositTx.GetHash().ToBytes()), 0, LightningMoney.Satoshis(50_000),
                                      100, walletAddress);

        // Mirror UnitOfWork.AddUtxo + UtxoMemoryRepository.Add, which throw on a duplicate outpoint
        var utxoSet = new Dictionary<(TxId, uint), UtxoModel>();
        var mockUtxoMemoryRepository = new Mock<IUtxoMemoryRepository>();
        mockUtxoMemoryRepository.Setup(x => x.Load(It.IsAny<List<UtxoModel>>()))
                                .Callback<List<UtxoModel>>(list => list.ForEach(u => utxoSet[(u.TxId, u.Index)] = u));
        UtxoModel? found;
        mockUtxoMemoryRepository
           .Setup(x => x.TryGetUtxo(It.IsAny<TxId>(), It.IsAny<uint>(), out found))
           .Returns(new TryGetUtxoCallback((TxId txId, uint index, out UtxoModel? utxo) =>
                                               utxoSet.TryGetValue((txId, index), out utxo)));
        _fakeServiceProvider.AddService(typeof(IUtxoMemoryRepository), mockUtxoMemoryRepository.Object);
        _mockUnitOfWork.Setup(x => x.AddUtxo(It.IsAny<UtxoModel>()))
                       .Callback<UtxoModel>(u =>
                        {
                            if (!utxoSet.TryAdd((u.TxId, u.Index), u))
                                throw new InvalidOperationException("Cannot add Utxo");
                        });

        _mockUtxoDbRepository.Setup(x => x.GetUnspentAsync(It.IsAny<bool>())).ReturnsAsync([knownUtxo]);
        _mockWalletAddressesDbRepository.Setup(x => x.GetAllAddresses()).Returns([walletAddress]);
        var service = CreateService(chain);
        var depositEvents = 0;
        service.OnWalletMovementDetected += (_, _) => depositEvents++;

        // Act
        await service.StartAsync(0, TestContext.Current.CancellationToken);
        await service.StopAsync();

        // Assert
        Assert.Equal(101u, service.LastProcessedBlockHeight);
        Assert.False(service.IsChainProcessingHalted);
        Assert.Equal(0, depositEvents);
        _mockUnitOfWork.Verify(x => x.AddUtxo(It.IsAny<UtxoModel>()), Times.Never);
    }

    [Fact]
    public async Task Given_LongBacklogAndSmallQueue_When_Starting_Then_AllMissedBlocksAreProcessedInOrder()
    {
        // Arrange
        var processedHeights = new List<uint>();
        _mockBlockchainStateRepository.Setup(x => x.Update(It.IsAny<BlockchainState>()))
                                      .Callback<BlockchainState>(s => processedHeights.Add(s.LastProcessedHeight));
        _service.MaxQueuedBlocks = 3;

        // Act
        await _service.StartAsync(0, TestContext.Current.CancellationToken);
        await _service.StopAsync();

        // Assert
        Assert.Equal(Enumerable.Range(100, 11).Select(h => (uint)h), processedHeights);
    }

    [Fact]
    public async Task Given_HaltedQueue_When_NewBlocksArrive_Then_QueueStaysBoundedAndRecoversWithoutSkipping()
    {
        // Arrange
        var chain = new FakeBitcoinChain(101);
        var failing = true;
        var processedHeights = new List<uint>();
        _mockBlockchainStateRepository.Setup(x => x.Update(It.IsAny<BlockchainState>()))
                                      .Callback<BlockchainState>(s =>
                                       {
                                           if (failing)
                                               throw new InvalidOperationException("db down");

                                           processedHeights.Add(s.LastProcessedHeight);
                                       });
        var service = CreateService(chain);
        service.MaxBlockProcessingAttempts = 1;
        service.BlockRetryBaseDelay = TimeSpan.Zero;
        service.MaxQueuedBlocks = 4;

        var queueField = typeof(BlockchainMonitorService).GetField("_blocksToProcess",
                             System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                      ?? throw new InvalidCastException("Can't find _blocksToProcess field");
        var queue = queueField.GetValue(service) as OrderedDictionary<uint, Block>
                 ?? throw new InvalidCastException("Can't get _blocksToProcess field");

        await service.StartAsync(0, TestContext.Current.CancellationToken);
        Assert.True(service.IsChainProcessingHalted);

        // Act: many blocks arrive while halted
        for (var height = 102u; height <= 120u; height++)
            await service.ProcessNewBlockAsync(chain.Mine(), height);

        // Assert
        Assert.True(queue.Count <= 4, $"Queue grew to {queue.Count} blocks while halted");

        // Act: the failure clears and one more block arrives
        failing = false;
        await service.ProcessNewBlockAsync(chain.Mine(), 121);
        await service.StopAsync();

        // Assert: every block from the halted one onwards is processed in order
        Assert.False(service.IsChainProcessingHalted);
        Assert.Equal(Enumerable.Range(100, 22).Select(h => (uint)h), processedHeights);
        Assert.Equal(121u, service.LastProcessedBlockHeight);
    }

    [Fact]
    public async Task Given_WatchedOutpoint_When_BlockSpendsIt_Then_SpendRecordedAndRaisedWithTheTransaction()
    {
        // Arrange (N10: a mutual close the peer broadcast without us recording it; O0-T2: any spend)
        await _service.StartAsync(0, TestContext.Current.CancellationToken);
        var channelId = new ChannelId(Enumerable.Repeat((byte)0x0e, 32).ToArray());
        var fundingTxId = new TxId(Enumerable.Repeat((byte)0x0f, 32).ToArray());
        _service.WatchOutpointSpend(channelId, fundingTxId, 1);
        var unrelated = Network.RegTest.CreateTransaction();
        unrelated.Inputs.Add(new OutPoint(new uint256((byte[])fundingTxId), 0));
        var spend = Network.RegTest.CreateTransaction();
        spend.Inputs.Add(new OutPoint(new uint256((byte[])fundingTxId), 1));
        spend.Outputs.Add(Money.Satoshis(10_000), new Key().PubKey.WitHash.ScriptPubKey);
        var raised = new List<OutpointSpentEventArgs>();
        _service.OnWatchedOutpointSpent += (_, args) => raised.Add(args);

        // Act
        var block = _chain.Mine(unrelated, spend);
        await _service.ProcessNewBlockAsync(block, 111);

        // Assert
        var args = Assert.Single(raised);
        var spendTxId = new TxId(spend.GetHash().ToBytes());
        Assert.Equal(channelId, args.ChannelId);
        Assert.Equal(spendTxId, args.SpendingTransaction.TxId);
        Assert.Equal(spend.ToBytes(), args.SpendingTransaction.RawTxBytes);
        Assert.Equal(111u, args.BlockHeight);
        Assert.Equal(2u, args.TransactionIndex);
        Assert.Equal(fundingTxId, args.SpentTransactionId);
        Assert.Equal(1u, args.SpentOutputIndex);
        var blockHash = new Hash(block.GetHash().ToBytes());
        Assert.Equal(blockHash, args.BlockHash);
        _mockWatchedOutpointRepository.Verify(x => x.MarkSpentAsync(fundingTxId, 1, spendTxId, 111, blockHash),
                                              Times.Once);
    }

    [Fact]
    public async Task Given_OutpointNoLongerWatched_When_BlockSpendsIt_Then_NothingRaised()
    {
        // Arrange
        await _service.StartAsync(0, TestContext.Current.CancellationToken);
        var fundingTxId = new TxId(Enumerable.Repeat((byte)0x0f, 32).ToArray());
        _service.WatchOutpointSpend(new ChannelId(new byte[32]), fundingTxId, 0);
        _service.StopWatchingOutpointSpend(fundingTxId, 0);
        var spend = Network.RegTest.CreateTransaction();
        spend.Inputs.Add(new OutPoint(new uint256((byte[])fundingTxId), 0));
        var raised = 0;
        _service.OnWatchedOutpointSpent += (_, _) => raised++;

        // Act
        await _service.ProcessNewBlockAsync(_chain.Mine(spend), 111);

        // Assert
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task Given_StoredWatchedOutpoints_When_Starting_Then_TheyAreWatched()
    {
        // Arrange
        var funding = CreateTransaction(0x20);
        var channelId = new ChannelId(Enumerable.Repeat((byte)0x21, 32).ToArray());
        _mockWatchedOutpointRepository
           .Setup(x => x.GetActiveAsync())
           .ReturnsAsync([
                new WatchedOutpointModel(new TxId(funding.GetHash().ToBytes()), 0, channelId,
                                         WatchedOutpointPurpose.FundingOutput)
            ]);
        var raised = new List<OutpointSpentEventArgs>();
        _service.OnWatchedOutpointSpent += (_, args) => raised.Add(args);

        // Act
        await _service.StartAsync(0, TestContext.Current.CancellationToken);
        await _service.ProcessNewBlockAsync(_chain.Mine(CreateSpend(funding, 0)), 111);

        // Assert
        Assert.Equal(channelId, Assert.Single(raised).ChannelId);
    }

    [Fact]
    public async Task Given_AddressWithADeposit_When_ASecondDepositArrives_Then_BothAreRecorded()
    {
        // Arrange - regression: the address was dropped from the watch list after its first deposit, so a second
        // channel closing to the same shutdown address never credited the wallet until a restart
        await _service.StartAsync(0, TestContext.Current.CancellationToken);
        var address = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        var walletAddress = new WalletAddressModel(AddressType.P2Wpkh, 0, false, address.ToString());
        _service.WatchBitcoinAddress(walletAddress);
        _fakeServiceProvider.AddService(typeof(IUtxoMemoryRepository), new Mock<IUtxoMemoryRepository>().Object);
        var added = new List<UtxoModel>();
        _mockUnitOfWork.Setup(x => x.AddUtxo(It.IsAny<UtxoModel>())).Callback<UtxoModel>(added.Add);
        var first = Network.RegTest.CreateTransaction();
        first.Outputs.Add(Money.Satoshis(40_000), address);
        var second = Network.RegTest.CreateTransaction();
        second.Outputs.Add(Money.Satoshis(60_000), address);
        var movements = 0;
        _service.OnWalletMovementDetected += (_, _) => movements++;

        // Act
        await _service.ProcessNewBlockAsync(_chain.Mine(first), 111);
        await _service.ProcessNewBlockAsync(_chain.Mine(second), 112);

        // Assert
        Assert.Equal([40_000L, 60_000L], added.Select(u => u.Amount.Satoshi));
        Assert.Equal(2, movements);
    }

    [Fact]
    public void Given_TrackedWatch_When_Tracked_Then_NothingWrittenAndItIsFollowed()
    {
        // Arrange: the caller saved the row in its own save (the closing transaction with Closing)
        var txId = new TxId(Enumerable.Repeat((byte)0x3c, 32).ToArray());
        var watch = new WatchedTransactionModel(new ChannelId(new byte[32]), txId, 6);

        // Act
        _service.TrackWatchedTransaction(watch);

        // Assert
        _mockWatchedTransactionRepository.Verify(x => x.Add(It.IsAny<WatchedTransactionModel>()), Times.Never);
        var watched = typeof(BlockchainMonitorService)
                     .GetField("_watchedTransactions",
                               System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                     .GetValue(_service) as ConcurrentDictionary<uint256, WatchedTransactionModel>;
        Assert.Same(watch, watched![new uint256((byte[])txId)]);
    }

    [Fact]
    public async Task Given_PublishAndWatch_When_Called_Then_TheWatchAndTheRawTransactionAreSavedTogetherBeforeTheSend()
    {
        // Arrange (NL-258)
        var transaction = CreateTransaction(0x30);
        var signed = ToSigned(transaction);
        var channelId = new ChannelId(Enumerable.Repeat((byte)0x31, 32).ToArray());
        _mockWatchedTransactionRepository.Setup(x => x.Add(It.IsAny<WatchedTransactionModel>()))
                                         .Callback(() => _steps.Add("add watch"));
        _mockBroadcastRepository.Setup(x => x.Add(It.IsAny<BroadcastTransactionModel>()))
                                .Callback((BroadcastTransactionModel b) => _steps.Add($"add {b.State}"));
        _chain.SendFailure = new InvalidOperationException("node down");

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.PublishAndWatchTransactionAsync(channelId, signed, 3));

        // Assert: saved before the (refused) send, which the caller still sees
        Assert.Equal(["add watch", "add Pending", "save"], _steps);
        Assert.Single(_chain.SendAttempts);
    }

    [Fact]
    public async Task Given_TransactionAlreadyInTheMempool_When_Published_Then_ItCountsAsAccepted()
    {
        // Arrange
        var broadcast = new BroadcastTransactionModel(ToSigned(CreateTransaction(0x32)), BroadcastPurpose.Sweep, null,
                                                      100);
        _chain.SendFailure = new InvalidOperationException("txn-already-in-mempool");

        // Act
        var accepted = await _service.PublishAsync(broadcast);

        // Assert
        Assert.True(accepted);
    }

    [Fact]
    public async Task Given_ASavedBroadcast_When_SavedAndPublishedAgain_Then_NoSecondRowIsAdded()
    {
        // Arrange
        var broadcast = new BroadcastTransactionModel(ToSigned(CreateTransaction(0x33)), BroadcastPurpose.Penalty,
                                                      null, 100);
        _mockBroadcastRepository.Setup(x => x.GetByTransactionIdAsync(broadcast.TransactionId)).ReturnsAsync(broadcast);

        // Act
        var accepted = await _service.SaveAndPublishAsync(broadcast);

        // Assert
        Assert.True(accepted);
        _mockBroadcastRepository.Verify(x => x.Add(It.IsAny<BroadcastTransactionModel>()), Times.Never);
        Assert.Single(_chain.SendAttempts);
    }

    [Fact]
    public void Given_UnknownNetwork_When_Constructed_Then_ItThrowsInsteadOfUsingMainnet()
    {
        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => CreateService(_chain, "notanetwork"));
    }

    [Fact]
    public void Given_BitcoinInfrastructure_When_ResolvingTheOnchainPorts_Then_TheyAreTheChainMonitor()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddBitcoinInfrastructure();
        var monitor = new Mock<IBlockchainMonitor>().Object;
        services.Replace(ServiceDescriptor.Singleton(monitor));
        using var provider = services.BuildServiceProvider();

        // Act & Assert
        Assert.Same(monitor, provider.GetRequiredService<IChainBroadcaster>());
        Assert.Same(monitor, provider.GetRequiredService<IOutpointWatcher>());
    }

    [Fact]
    public async Task Given_ABroadcastTheNodeKeepsRefusing_When_BlocksArrive_Then_ItIsWarnedAboutFirstAndThenPeriodically()
    {
        // Arrange: a stored funding transaction the node refuses for good (e.g. its inputs are gone)
        var logger = new RecordingLogger();
        var service = CreateService(_chain, logger: logger);
        service.RefusalWarningInterval = 3;
        await service.StartAsync(0, TestContext.Current.CancellationToken);
        var broadcast = new BroadcastTransactionModel(ToSigned(CreateTransaction(0x34)), BroadcastPurpose.Funding,
                                                      null, 110);
        _chain.SendFailure = new InvalidOperationException("bad-txns-inputs-missingorspent");

        // Act: the first send, then six blocks (a rebroadcast after each)
        Assert.False(await service.PublishAsync(broadcast));
        for (var i = 0; i < 6; i++)
            await service.ProcessNewBlockAsync(_chain.Mine(), _chain.TipHeight);
        await service.StopAsync();

        // Assert: 7 refusals, a warning at the 1st, 3rd and 6th, the others at Debug
        var refusals = logger.Entries.Where(e => e.Message.Contains("was refused")).ToList();
        Assert.Equal(7, refusals.Count);
        Assert.Equal([
                         LogLevel.Warning, LogLevel.Debug, LogLevel.Warning, LogLevel.Debug, LogLevel.Debug,
                         LogLevel.Warning, LogLevel.Debug
                     ], refusals.Select(e => e.Level));
        Assert.Contains("(6 time(s) in a row)", refusals[5].Message);

        // Act: the node accepts it again, then refuses it again
        _chain.SendFailure = null;
        await service.PublishAsync(broadcast);
        _chain.SendFailure = new InvalidOperationException("node down");
        await service.PublishAsync(broadcast);

        // Assert: the count started over
        var (level, message) = logger.Entries.Last(e => e.Message.Contains("was refused"));
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("(1 time(s) in a row)", message);
    }

    private BlockchainMonitorService CreateService(FakeBitcoinChain chain, string network = "regtest",
                                                   ILogger<BlockchainMonitorService>? logger = null)
    {
        var bitcoinOptions = new Mock<IOptions<BitcoinOptions>>();
        bitcoinOptions.Setup(x => x.Value).Returns(new BitcoinOptions
        {
            RpcEndpoint = "",
            RpcUser = "",
            RpcPassword = "",
            ZmqHost = "127.0.0.1",
            ZmqBlockPort = 28332,
            ZmqTxPort = 28333
        });
        var nodeOptions = new Mock<IOptions<NodeOptions>>();
        nodeOptions.Setup(x => x.Value).Returns(new NodeOptions { BitcoinNetwork = network });
        return new BlockchainMonitorService(bitcoinOptions.Object, chain,
                                            logger ?? new Mock<ILogger<BlockchainMonitorService>>().Object,
                                            nodeOptions.Object,
                                            _fakeServiceProvider)
        {
            BlockRetryBaseDelay = TimeSpan.Zero
        };
    }

    private static Transaction CreateTransaction(byte seed)
    {
        var transaction = Network.RegTest.CreateTransaction();
        transaction.Inputs.Add(new OutPoint(new uint256(Enumerable.Repeat(seed, 32).ToArray()), seed));
        transaction.Outputs.Add(Money.Satoshis(100_000), new Key().PubKey.WitHash.ScriptPubKey);
        return transaction;
    }

    private static Transaction CreateSpend(Transaction spent, uint outputIndex)
    {
        var transaction = Network.RegTest.CreateTransaction();
        transaction.Inputs.Add(new OutPoint(spent.GetHash(), outputIndex));
        transaction.Outputs.Add(Money.Satoshis(10_000), new Key().PubKey.WitHash.ScriptPubKey);
        return transaction;
    }

    private static SignedTransaction ToSigned(Transaction transaction) =>
        new(new TxId(transaction.GetHash().ToBytes()), transaction.ToBytes());

    private delegate bool TryGetUtxoCallback(TxId txId, uint index, out UtxoModel? utxo);

    /// <summary>Keeps every log entry at every level (a Moq logger reports every level as disabled).</summary>
    private sealed class RecordingLogger : ILogger<BlockchainMonitorService>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            Entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}