namespace NLightning.Integration.Tests.Persistence;

using Domain.Channels.ValueObjects;
using Domain.Money;
using Infrastructure.Repositories.Memory;

/// <summary>
/// The fee reservations of <see cref="UtxoMemoryRepository"/> (BOLT 5 plan O7-T1): all or nothing, never together with a
/// channel lock, kept across a reorg's remove and re-add.
/// </summary>
public class UtxoMemoryRepositoryFeeReservationTests
{
    [Fact]
    public void Given_OneOutpointTaken_When_Reserving_Then_NothingIsReserved()
    {
        // Arrange
        var repository = new UtxoMemoryRepository();
        var free = SqliteTestDatabase.CreateUtxo(SqliteTestDatabase.CreateWalletAddress(), 1);
        var locked = SqliteTestDatabase.CreateUtxo(SqliteTestDatabase.CreateWalletAddress(1), 2);
        locked.LockedToChannelId = ChannelId.Zero;
        repository.Load([free, locked]);

        // Act
        var reserved = repository.TryReserveForFee([(free.TxId, free.Index), (locked.TxId, locked.Index)],
                                                   Guid.NewGuid());

        // Assert
        Assert.False(reserved);
        Assert.False(repository.TryGetFeeReservation(free.TxId, free.Index, out _));
    }

    [Fact]
    public void Given_AReservedOutput_When_AChannelLocksFunds_Then_TheReservedOutputIsNotUsed()
    {
        // Arrange
        var repository = new UtxoMemoryRepository();
        var reservedUtxo = SqliteTestDatabase.CreateUtxo(SqliteTestDatabase.CreateWalletAddress(), 1, amountSats: 90_000);
        var other = SqliteTestDatabase.CreateUtxo(SqliteTestDatabase.CreateWalletAddress(1), 2, amountSats: 50_000);
        repository.Load([reservedUtxo, other]);
        Assert.True(repository.TryReserveForFee([(reservedUtxo.TxId, reservedUtxo.Index)], Guid.NewGuid()));

        // Act
        var locked = repository.LockUtxosToSpendOnChannel(LightningMoney.Satoshis(40_000), ChannelId.Zero);

        // Assert
        Assert.Equal(other.TxId, Assert.Single(locked).TxId);
        Assert.Null(reservedUtxo.LockedToChannelId);
        Assert.Throws<InvalidOperationException>(
            () => repository.LockUtxosToSpendOnChannel(LightningMoney.Satoshis(1_000), ChannelId.Zero));
        Assert.Equal(140_000, repository.GetLockedBalance().Satoshi);
        Assert.Empty(repository.GetUnreservedUtxos());
    }

    [Fact]
    public void Given_AReservation_When_TheOutputIsRemovedAndAddedBack_Then_ItStaysReservedUntilReleased()
    {
        // Arrange: a reorg removes a deposit and adds it back when it confirms again (NL-293)
        var repository = new UtxoMemoryRepository();
        var utxo = SqliteTestDatabase.CreateUtxo(SqliteTestDatabase.CreateWalletAddress());
        repository.Add(utxo);
        var reservationId = Guid.NewGuid();
        Assert.True(repository.TryReserveForFee([(utxo.TxId, utxo.Index)], reservationId));

        // Act
        repository.Spend(utxo);
        repository.Add(SqliteTestDatabase.CreateUtxo(SqliteTestDatabase.CreateWalletAddress()));

        // Assert
        Assert.True(repository.TryGetFeeReservation(utxo.TxId, utxo.Index, out var id));
        Assert.Equal(reservationId, id);
        Assert.Empty(repository.GetUnreservedUtxos());
        repository.ReleaseFeeReservation(reservationId);
        Assert.Single(repository.GetUnreservedUtxos());
    }
}