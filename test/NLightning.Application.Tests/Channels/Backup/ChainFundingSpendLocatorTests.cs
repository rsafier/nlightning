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
    public async Task Given_ABackupWithoutShortChannelId_When_Located_Then_NotConfirmedAndTheChainIsNotRead()
    {
        // Act
        var location = await CreateLocator().LocateAsync(_entry with { ShortChannelId = null },
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

        // Act
        var location = await CreateLocator().LocateAsync(_entry, TestContext.Current.CancellationToken);

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
        _chain.Verify(c => c.GetBlockAsync(It.IsAny<uint>()), Times.Exactly(10));
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

    private ChainFundingSpendLocator CreateLocator(uint depth = 4032) =>
        new(_chain.Object, Options.Create(new ChannelBackupOptions { RestoreSpendSearchDepth = depth }));

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