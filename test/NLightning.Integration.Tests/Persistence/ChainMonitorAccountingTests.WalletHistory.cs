using Microsoft.EntityFrameworkCore;
using NBitcoin;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.Wallet.Models;
using Infrastructure.Repositories.Database.Bitcoin;

/// <summary>
/// The wallet's durable transaction history (NL-1187, table <c>WalletTransactions</c>) as the chain monitor writes it
/// on the real SQLite schema: in each block's save, with the raw transaction, the block and the wallet's outputs and
/// inputs, independent of the accounting feed's gate, kept whole by a replayed block and made unconfirmed by a reorg's
/// rewind save until the new branch confirms it again.
/// </summary>
public partial class ChainMonitorAccountingTests
{
    [Fact]
    public async Task Given_ADepositAndOurWithdrawal_When_TheirBlocksAreProcessedAndReplayed_Then_TheWalletHistoryHoldsBoth()
    {
        // Arrange: 101 holds a deposit to the wallet, 102 our withdrawal spending it with change
        await using var harness = new ChainMonitorHarness();
        var wallet = await SeedWalletAsync(harness, 2);
        await harness.StartAsync(95);
        var deposit = CreateDeposit(0x21, wallet[0], DepositSat);
        var depositBlock = await harness.MineAndDeliverAsync(deposit);
        var external = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        var withdrawal = CreateWithdrawal(deposit, 1, wallet[0], external, wallet[1]);
        await harness.Monitor.SaveAndPublishAsync(WalletSendRow(withdrawal));

        // Act
        var withdrawalBlock = await harness.MineAndDeliverAsync();
        await harness.RestartAsync();
        await harness.DeliverTipAsync();

        // Assert: both rows, the spend's input kept although the replay no longer finds the spent output
        var history = await LoadWalletHistoryAsync(harness);
        Assert.Equal(2, history.Count);
        var depositRow = Assert.Single(history, r => r.TxId == TxIdOf(deposit));
        Assert.Equal(101u, depositRow.BlockHeight);
        Assert.Equal(depositBlock.GetHash().ToBytes(), depositRow.BlockHash);
        Assert.Equal(deposit.ToBytes(), depositRow.RawTransaction);
        Assert.Equal(new uint[] { 1 }, depositRow.OurOutputs);
        Assert.Empty(depositRow.OurInputs);
        Assert.Equal(depositBlock.Header.BlockTime, depositRow.Timestamp);
        var withdrawalRow = Assert.Single(history, r => r.TxId == TxIdOf(withdrawal));
        Assert.Equal(102u, withdrawalRow.BlockHeight);
        Assert.Equal(withdrawalBlock.GetHash().ToBytes(), withdrawalRow.BlockHash);
        Assert.Equal(new uint[] { 1 }, withdrawalRow.OurOutputs);
        Assert.Equal(new[] { new WalletTransactionInput(0, DepositSat) }, withdrawalRow.OurInputs);
    }

    [Fact]
    public async Task Given_TheAccountingFeedGateHeld_When_ADepositConfirms_Then_TheWalletHistoryStillRecordsIt()
    {
        // Arrange: the cutover failed in this process, so the feed drops live events (NL-619)
        await using var harness = new ChainMonitorHarness();
        var wallet = await SeedWalletAsync(harness, 1);
        await harness.StartAsync(95);
        harness.FeedGate.Hold("cutover failed");
        var deposit = CreateDeposit(0x22, wallet[0], DepositSat);

        // Act
        await harness.MineAndDeliverAsync(deposit);

        // Assert
        Assert.Empty(await LoadEventsAsync(harness));
        var row = Assert.Single(await LoadWalletHistoryAsync(harness));
        Assert.Equal(TxIdOf(deposit), row.TxId);
        Assert.Equal(101u, row.BlockHeight);
    }

    [Fact]
    public async Task Given_AReorgOfOurWithdrawal_When_TheChainRewinds_Then_ItsHistoryRowIsUnconfirmedUntilItConfirmsAgain()
    {
        // Arrange: 101 holds a deposit, 102 our withdrawal spending it (with change)
        await using var harness = new ChainMonitorHarness();
        var wallet = await SeedWalletAsync(harness, 2);
        await harness.StartAsync(95);
        var deposit = CreateDeposit(0x23, wallet[0], DepositSat);
        await harness.MineAndDeliverAsync(deposit);
        var external = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        var withdrawal = CreateWithdrawal(deposit, 1, wallet[0], external, wallet[1]);
        await harness.Monitor.SaveAndPublishAsync(WalletSendRow(withdrawal));
        await harness.MineAndDeliverAsync();

        // Act: a branch from 100 whose first block holds the deposit alone becomes the active chain
        harness.Chain.Reorg(100, 3, deposit);
        await harness.DeliverTipAsync();

        // Assert: the withdrawal is unconfirmed (the rewind's save); the deposit is confirmed on the new branch
        var history = await LoadWalletHistoryAsync(harness);
        var withdrawalRow = Assert.Single(history, r => r.TxId == TxIdOf(withdrawal));
        Assert.Null(withdrawalRow.BlockHeight);
        Assert.Null(withdrawalRow.BlockHash);
        Assert.Equal(new[] { new WalletTransactionInput(0, DepositSat) }, withdrawalRow.OurInputs);
        var depositRow = Assert.Single(history, r => r.TxId == TxIdOf(deposit));
        Assert.Equal(101u, depositRow.BlockHeight);
        Assert.Equal(harness.Chain[101].GetHash().ToBytes(), depositRow.BlockHash);

        // Act: the withdrawal (sent again after the rewind) confirms on the new branch
        var again = await harness.MineAndDeliverAsync();

        // Assert
        withdrawalRow = Assert.Single(await LoadWalletHistoryAsync(harness), r => r.TxId == TxIdOf(withdrawal));
        Assert.Equal(104u, withdrawalRow.BlockHeight);
        Assert.Equal(again.GetHash().ToBytes(), withdrawalRow.BlockHash);
        Assert.Equal(new[] { new WalletTransactionInput(0, DepositSat) }, withdrawalRow.OurInputs);
    }

    [Fact]
    public async Task Given_StoredHistory_When_ReadByHeightRange_Then_OnlyTheRangeAndOptionallyTheUnconfirmedAreReturned()
    {
        // Arrange
        await using var harness = new ChainMonitorHarness();
        await using (var context = harness.Context())
        {
            var repository = new WalletTransactionDbRepository(context);
            await repository.StageConfirmedAsync(Record(0x31, 101));
            await repository.StageConfirmedAsync(Record(0x32, 120));
            await repository.StageConfirmedAsync(Record(0x33, 130));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            await repository.UnconfirmAboveAsync(125);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act
        await using var reader = harness.Context();
        var repositoryReader = new WalletTransactionDbRepository(reader);
        var confirmed = await repositoryReader.GetHistoryAsync(110, 140, false, TestContext.Current.CancellationToken);
        var all = await repositoryReader.GetHistoryAsync(0, uint.MaxValue, true, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(120u, Assert.Single(confirmed).BlockHeight);
        Assert.Equal(3, all.Count);
        Assert.Single(all, r => r.BlockHeight is null);
    }

    private static WalletTransactionRecord Record(byte seed, uint height)
    {
        var transaction = Network.RegTest.CreateTransaction();
        transaction.Inputs.Add(new OutPoint(new uint256(Enumerable.Repeat(seed, 32).ToArray()), 0));
        transaction.Outputs.Add(Money.Satoshis(10_000), new Key().PubKey.WitHash.ScriptPubKey);
        return new WalletTransactionRecord(TxIdOf(transaction), transaction.ToBytes(), height, new byte[32],
                                           DateTimeOffset.UnixEpoch, [0], [new WalletTransactionInput(0, 11_000)]);
    }

    private static async Task<List<WalletTransactionRecord>> LoadWalletHistoryAsync(ChainMonitorHarness harness)
    {
        await using var context = harness.Context();
        Assert.Equal(await context.WalletTransactions.CountAsync(TestContext.Current.CancellationToken),
                     (await new WalletTransactionDbRepository(context)
                         .GetHistoryAsync(0, uint.MaxValue, true, TestContext.Current.CancellationToken)).Count);
        return (await new WalletTransactionDbRepository(context)
                   .GetHistoryAsync(0, uint.MaxValue, true, TestContext.Current.CancellationToken))
              .ToList();
    }
}