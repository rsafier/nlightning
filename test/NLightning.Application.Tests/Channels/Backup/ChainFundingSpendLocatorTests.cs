using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Tests.Channels.Backup;

using Application.Channels.Backup;
using Application.Channels.Backup.Models;
using Domain.Channels.ValueObjects;
using Infrastructure.Bitcoin.Wallet.Interfaces;

public class ChainFundingSpendLocatorTests
{
    private const uint Tip = 1_000;

    private readonly Mock<IBitcoinChainService> _chain = new();
    private readonly Dictionary<uint, Block> _blocks = [];
    private readonly ChannelBackupEntry _entry;
    private readonly OutPoint _funding;

    public ChainFundingSpendLocatorTests()
    {
        var data = new BackupTestData();
        var channel = data.AddChannel(1, scid: new ShortChannelId(900, 1, 0));
        _entry = ChannelBackupService.CreateEntry(channel, data.Peers[0]);
        _funding = new OutPoint(new uint256((byte[])_entry.FundingTxId), _entry.FundingOutputIndex);
        _chain.Setup(c => c.GetCurrentBlockHeightAsync()).ReturnsAsync(Tip);
        _chain.Setup(c => c.GetBlockAsync(It.IsAny<uint>()))
              .ReturnsAsync((uint height) => _blocks.TryGetValue(height, out var block) ? block : EmptyBlock(height));
    }

    [Fact]
    public async Task Given_AnUnspentFundingOutput_When_Located_Then_Unspent()
    {
        // Arrange
        _chain.Setup(c => c.GetConfirmedUnspentOutputAsync(_funding))
              .ReturnsAsync((new TxOut(Money.Satoshis(1_000_001), new Script()), 900u));

        // Act
        var location = await CreateLocator().LocateAsync(_entry, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FundingSpendStatus.Unspent, location.Status);
        _chain.Verify(c => c.GetBlockAsync(It.IsAny<uint>()), Times.Never);
    }

    [Fact]
    public async Task Given_ABackupWithoutShortChannelIdOrFundingHeight_When_Located_Then_NotConfirmedAndTheChainIsNotRead()
    {
        // Act
        var location = await CreateLocator().LocateAsync(_entry with { ShortChannelId = null, FundingHeight = 0 },
                                                         TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FundingSpendStatus.NotConfirmed, location.Status);
        _chain.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_TheFundingSpentBeforeTheRestore_When_Located_Then_TheSpendIsFoundAsTheMonitorWouldReportIt()
    {
        // Arrange: the peer's commitment mined three blocks below the tip, second transaction of its block
        var spend = SpendingTransaction();
        var block = EmptyBlock(Tip - 3);
        block.AddTransaction(spend);
        _blocks[Tip - 3] = block;

        // Act: one block at a time
        var location = await CreateLocator(batchSize: 1).LocateAsync(_entry, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FundingSpendStatus.SpentFound, location.Status);
        var args = Assert.IsType<Domain.Bitcoin.Events.OutpointSpentEventArgs>(location.Spend);
        Assert.Equal(_entry.ChannelId, args.ChannelId);
        Assert.Equal(spend.GetHash().ToBytes(), (byte[])args.SpendingTransaction.TxId);
        Assert.Equal(spend.ToBytes(), args.SpendingTransaction.RawTxBytes);
        Assert.Equal(Tip - 3, args.BlockHeight);
        Assert.Equal(1u, args.TransactionIndex);
        Assert.Equal(_entry.FundingTxId, args.SpentTransactionId);
        Assert.Equal((uint)_entry.FundingOutputIndex, args.SpentOutputIndex);
        Assert.Equal(block.GetHash().ToBytes(), (byte[])args.BlockHash!.Value);

        // Searched from the tip down, stopping at the spend
        _chain.Verify(c => c.GetBlockAsync(Tip - 4), Times.Never);
    }

    [Fact]
    public async Task Given_ASpendOlderThanTheSearchDepth_When_Located_Then_SpentNotFoundWithTheLowestSearchedHeight()
    {
        // Arrange: depth 10, the spend 20 blocks below the tip
        var block = EmptyBlock(Tip - 20);
        block.AddTransaction(SpendingTransaction());
        _blocks[Tip - 20] = block;

        // Act
        var location = await CreateLocator(depth: 10).LocateAsync(_entry, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FundingSpendStatus.SpentNotFound, location.Status);
        Assert.Equal(Tip - 9, location.SearchedFromHeight);
        Assert.Equal(900u, location.FloorHeight);
        Assert.True(location.HasOlderBlocksToSearch);
        _chain.Verify(c => c.GetBlockAsync(It.IsAny<uint>()), Times.Exactly(10));
    }

    [Fact]
    public async Task Given_ASpendOlderThanTheSearchDepth_When_RescannedBelowTheWindow_Then_TheSpendIsFound()
    {
        // Arrange: depth 10, the spend 20 blocks below the tip (NL-430)
        var block = EmptyBlock(Tip - 20);
        block.AddTransaction(SpendingTransaction());
        _blocks[Tip - 20] = block;

        // Act
        var location = await CreateLocator(depth: 10).RescanAsync(_entry, Tip - 9,
                                                                  TestContext.Current.CancellationToken);

        // Assert: found, and the window already searched is not read again
        Assert.Equal(FundingSpendStatus.SpentFound, location.Status);
        Assert.Equal(Tip - 20, location.Spend!.BlockHeight);
        _chain.Verify(c => c.GetBlockAsync(Tip - 9), Times.Never);
    }

    [Fact]
    public async Task Given_NoSpendDownToTheFundingBlock_When_Rescanned_Then_SpentNotFoundWithNothingLeftToSearch()
    {
        // Act: from below 991 down to the funding block 900
        var location = await CreateLocator(depth: 10).RescanAsync(_entry, Tip - 9,
                                                                  TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FundingSpendStatus.SpentNotFound, location.Status);
        Assert.Equal(900u, location.SearchedFromHeight);
        Assert.False(location.HasOlderBlocksToSearch);
        _chain.Verify(c => c.GetBlockAsync(900u), Times.Once);
        _chain.Verify(c => c.GetBlockAsync(899u), Times.Never);
        _chain.Verify(c => c.GetBlockAsync(It.IsAny<uint>()), Times.Exactly(91));
    }

    [Fact]
    public async Task Given_ARescanBelowTheFundingBlock_When_Rescanned_Then_NothingIsRead()
    {
        // Act
        var location = await CreateLocator().RescanAsync(_entry, 900, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FundingSpendStatus.SpentNotFound, location.Status);
        Assert.False(location.HasOlderBlocksToSearch);
        _chain.Verify(c => c.GetBlockAsync(It.IsAny<uint>()), Times.Never);
    }

    [Fact]
    public async Task Given_ABatchSize_When_Located_Then_BlocksAreReadABatchAtATimeFromTheTop()
    {
        // Arrange: batches of 4, the spend in the second batch (tip - 5)
        var block = EmptyBlock(Tip - 5);
        block.AddTransaction(SpendingTransaction());
        _blocks[Tip - 5] = block;

        // Act
        var location = await CreateLocator(depth: 100, batchSize: 4).LocateAsync(_entry,
                                                                                 TestContext.Current.CancellationToken);

        // Assert: blocks tip .. tip - 7 read, nothing below
        Assert.Equal(FundingSpendStatus.SpentFound, location.Status);
        Assert.Equal(Tip - 5, location.Spend!.BlockHeight);
        _chain.Verify(c => c.GetBlockAsync(Tip - 7), Times.Once);
        _chain.Verify(c => c.GetBlockAsync(Tip - 8), Times.Never);
    }

    [Fact]
    public async Task Given_NoShortChannelIdButAFundingHeight_When_Located_Then_TheSpendIsSearchedDownToTheFundingHeight()
    {
        // Arrange: the backup was written before the funding confirmed (funding height 101); the spend at 500
        var entry = _entry with { ShortChannelId = null };
        var block = EmptyBlock(500);
        block.AddTransaction(SpendingTransaction());
        _blocks[500] = block;

        // Act
        var location = await CreateLocator().LocateAsync(entry, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(101u, entry.FundingHeight);
        Assert.Equal(FundingSpendStatus.SpentFound, location.Status);
        Assert.Equal(500u, location.Spend!.BlockHeight);
    }

    [Fact]
    public async Task Given_NoShortChannelIdAndTheFundingInNoBlock_When_Located_Then_NotConfirmed()
    {
        // Act: funding height 101, no block holds the funding or a spend of it
        var location = await CreateLocator().LocateAsync(_entry with { ShortChannelId = null },
                                                         TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FundingSpendStatus.NotConfirmed, location.Status);
        Assert.Equal(101u, location.FloorHeight);
        _chain.Verify(c => c.GetBlockAsync(100u), Times.Never);
    }

    [Fact]
    public async Task Given_NoShortChannelIdAndTheFundingBlockWithoutSpendAbove_When_Located_Then_TheSearchStopsThere()
    {
        // Arrange: the funding transaction confirmed at 300 (not recorded in the backup)
        var funding = Network.RegTest.CreateTransaction();
        funding.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        funding.Outputs.Add(new TxOut(Money.Satoshis(1_000_001), new Script()));
        var entry = _entry with
        {
            ShortChannelId = null,
            FundingTxId = new Domain.Bitcoin.ValueObjects.TxId(funding.GetHash().ToBytes())
        };
        var block = EmptyBlock(300);
        block.AddTransaction(funding);
        _blocks[300] = block;

        // Act
        var location = await CreateLocator(batchSize: 1).LocateAsync(entry, TestContext.Current.CancellationToken);

        // Assert: spent (gettxout has no output) but not above its block, and nothing below it is read
        Assert.Equal(FundingSpendStatus.SpentNotFound, location.Status);
        Assert.Equal(300u, location.FloorHeight);
        Assert.False(location.HasOlderBlocksToSearch);
        _chain.Verify(c => c.GetBlockAsync(299u), Times.Never);
    }

    [Fact]
    public async Task Given_ADepthBelowTheFundingBlock_When_Located_Then_TheSearchStopsAtTheFundingBlock()
    {
        // Act: the funding block is 900, the tip 1000, the depth 4032
        var location = await CreateLocator().LocateAsync(_entry, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FundingSpendStatus.SpentNotFound, location.Status);
        Assert.Equal(900u, location.SearchedFromHeight);
        _chain.Verify(c => c.GetBlockAsync(899), Times.Never);
    }

    [Fact]
    public async Task Given_AChainError_When_Located_Then_ChainUnavailableWithTheReason()
    {
        // Arrange
        _chain.Setup(c => c.GetConfirmedUnspentOutputAsync(It.IsAny<OutPoint>()))
              .ThrowsAsync(new InvalidOperationException("rpc down"));

        // Act
        var location = await CreateLocator().LocateAsync(_entry, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FundingSpendStatus.ChainUnavailable, location.Status);
        Assert.Equal("rpc down", location.Error);
    }

    private ChainFundingSpendLocator CreateLocator(uint depth = 4032, uint batchSize = 8) =>
        new(_chain.Object, Options.Create(new ChannelBackupOptions
        {
            RestoreSpendSearchDepth = depth,
            RestoreSpendSearchBatchSize = batchSize
        }));

    private Transaction SpendingTransaction()
    {
        var transaction = Network.RegTest.CreateTransaction();
        transaction.Inputs.Add(new TxIn(_funding));
        transaction.Outputs.Add(new TxOut(Money.Satoshis(990_000), new Key().PubKey.WitHash.ScriptPubKey));
        return transaction;
    }

    private static Block EmptyBlock(uint height)
    {
        var block = Network.RegTest.Consensus.ConsensusFactory.CreateBlock();
        var coinbase = Network.RegTest.CreateTransaction();
        coinbase.Inputs.Add(new TxIn(new Script(Op.GetPushOp(height))));
        coinbase.Outputs.Add(new TxOut(Money.Coins(1), new Key().PubKey.WitHash.ScriptPubKey));
        block.AddTransaction(coinbase);
        block.Header.HashPrevBlock = new uint256(height);
        return block;
    }
}