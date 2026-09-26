using NBitcoin;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Infrastructure.Repositories.Database.Onchain;
using static ChainWatchSchemaRoundTrip;

/// <summary>
/// Migration <c>AddBroadcastCommitmentNumber</c> (NL-271, NL-297) on the real SQLite schema: a commitment broadcast
/// keeps its commitment number, the channel's broadcasts are listed, and a pending one can be abandoned.
/// </summary>
public class BroadcastCommitmentNumberTests
{
    [Fact]
    public async Task Given_LocalCommitmentBroadcast_When_Reloaded_Then_ItKeepsItsCommitmentNumber()
    {
        // Arrange
        await using var harness = new ChainMonitorHarness();
        await harness.StartAsync(95);
        var channelId = ChannelIdOf(0x51);
        var commitment = ChainMonitorPersistenceTests.CreateTransaction(0x51);
        var funding = ChainMonitorPersistenceTests.CreateTransaction(0x52);
        await using (var context = harness.Context())
        {
            var repository = new BroadcastTransactionDbRepository(context);
            repository.Add(new BroadcastTransactionModel(ToSigned(funding), BroadcastPurpose.Funding, channelId, 90));
            repository.Add(new BroadcastTransactionModel(ToSigned(commitment), BroadcastPurpose.LocalCommitment,
                                                         channelId, 100, commitmentNumber: 0xFFFF_FFFF_FFFF));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act
        IReadOnlyList<BroadcastTransactionModel> stored;
        await using (var context = harness.Context())
            stored = await new BroadcastTransactionDbRepository(context).GetByChannelIdAsync(channelId);

        // Assert
        Assert.Equal(2, stored.Count);
        var local = Assert.Single(stored, b => b.Purpose == BroadcastPurpose.LocalCommitment);
        Assert.Equal(0xFFFF_FFFF_FFFFUL, local.CommitmentNumber);
        Assert.Null(Assert.Single(stored, b => b.Purpose == BroadcastPurpose.Funding).CommitmentNumber);
    }

    [Fact]
    public async Task Given_PendingBroadcast_When_Abandoned_Then_ItLeavesThePendingSetAndOnlyOnce()
    {
        // Arrange
        await using var harness = new ChainMonitorHarness();
        await harness.StartAsync(95);
        var commitment = ChainMonitorPersistenceTests.CreateTransaction(0x53);
        var txId = new TxId(commitment.GetHash().ToBytes());
        await using (var context = harness.Context())
        {
            new BroadcastTransactionDbRepository(context)
               .Add(new BroadcastTransactionModel(ToSigned(commitment), BroadcastPurpose.LocalCommitment,
                                                  ChannelIdOf(0x53), 100, commitmentNumber: 3));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act
        bool first, second;
        await using (var context = harness.Context())
        {
            var repository = new BroadcastTransactionDbRepository(context);
            first = await repository.MarkAbandonedAsync(txId);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            second = await repository.MarkAbandonedAsync(txId);
        }

        // Assert
        Assert.True(first);
        Assert.False(second);
        await using var readContext = harness.Context();
        var repositoryAfter = new BroadcastTransactionDbRepository(readContext);
        Assert.Equal(BroadcastState.Abandoned, (await repositoryAfter.GetByTransactionIdAsync(txId))!.State);
        Assert.DoesNotContain(await repositoryAfter.GetPendingAsync(), b => b.TransactionId == txId);
    }

    private static SignedTransaction ToSigned(Transaction transaction) =>
        new(new TxId(transaction.GetHash().ToBytes()), transaction.ToBytes());
}