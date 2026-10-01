namespace NLightning.Integration.Tests.Persistence;

using Domain.Channels.ValueObjects;
using Infrastructure.Repositories.Memory;

/// <summary>
/// The channel locks of <see cref="UtxoMemoryRepository"/> (NL-462): restored at a restart for a funder waiting for
/// its funding confirmation, and released by the NL-259 paths as before.
/// </summary>
public class UtxoMemoryRepositoryChannelLockTests
{
    [Fact]
    public void Given_FreeWalletOutputs_When_RestoringTheFundingLocks_Then_TheyAreLockedToTheChannelAgain()
    {
        // Arrange
        var repository = new UtxoMemoryRepository();
        var first = SqliteTestDatabase.CreateUtxo(SqliteTestDatabase.CreateWalletAddress(), 1, 0, 90_000);
        var second = SqliteTestDatabase.CreateUtxo(SqliteTestDatabase.CreateWalletAddress(1), 2, 1, 30_000);
        repository.Load([first, second]);

        // Act
        var locked = repository.RestoreLocksForChannel(ChannelId.Zero,
                                                       [(first.TxId, first.Index), (second.TxId, second.Index)]);

        // Assert
        Assert.Equal(2, locked);
        Assert.Equal(2, repository.GetLockedUtxosForChannel(ChannelId.Zero).Count);
        Assert.Equal(120_000, repository.GetLockedBalance().Satoshi);
        Assert.Empty(repository.GetUnreservedUtxos());
    }

    [Fact]
    public void Given_ARestoredLock_When_TheFundingIsAbandonedOrForgotten_Then_TheReleaseStillReturnsTheOutput()
    {
        // Arrange: the NL-259 release paths (an abandoned funding, a forgotten funder) release by channel
        var repository = new UtxoMemoryRepository();
        var input = SqliteTestDatabase.CreateUtxo(SqliteTestDatabase.CreateWalletAddress(), 1, 0, 90_000);
        repository.Load([input]);
        Assert.Equal(1, repository.RestoreLocksForChannel(ChannelId.Zero, [(input.TxId, input.Index)]));

        // Act
        var released = repository.ReturnUtxosNotSpentOnChannel(ChannelId.Zero);

        // Assert
        Assert.Equal([input.TxId], released.Select(u => u.TxId));
        Assert.Null(input.LockedToChannelId);
        Assert.Equal(0, repository.GetLockedBalance().Satoshi);
        Assert.Single(repository.GetUnreservedUtxos());
    }

    [Fact]
    public void Given_ASpentOrForeignLockedOutpoint_When_Restoring_Then_ItIsSkipped()
    {
        // Arrange: an output the funding spent in a processed block is no longer in the wallet; one locked to another
        // channel is never taken over
        var repository = new UtxoMemoryRepository();
        var foreign = SqliteTestDatabase.CreateUtxo(SqliteTestDatabase.CreateWalletAddress(), 2, 0, 90_000);
        var otherChannel = new ChannelId([.. Enumerable.Repeat((byte)0x0a, 32)]);
        foreign.LockedToChannelId = otherChannel;
        repository.Load([foreign]);

        var spentTxId = SqliteTestDatabase.CreateUtxo(SqliteTestDatabase.CreateWalletAddress(1), 3).TxId;
        var locked = repository.RestoreLocksForChannel(ChannelId.Zero,
                                                       [(spentTxId, 0), (foreign.TxId, foreign.Index)]);

        // Assert
        Assert.Equal(0, locked);
        Assert.Equal(otherChannel, foreign.LockedToChannelId);
        Assert.Empty(repository.GetLockedUtxosForChannel(ChannelId.Zero));
    }
}
