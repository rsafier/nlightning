namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.Wallet.Models;
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
    public void Given_TransientMempoolCoin_When_ReservedEvictedPromotedAndReorged_Then_ConfirmedAccountingAndReservationIdentityStaySeparate()
    {
        // Arrange
        var repository = new UtxoMemoryRepository();
        var mined = SqliteTestDatabase.CreateUtxo(SqliteTestDatabase.CreateWalletAddress());
        var transient = new UtxoModel(mined.TxId, mined.Index, mined.Amount, 0, mined.WalletAddress!);
        repository.AddUnconfirmed(transient);
        // Assert: even a large tip cannot turn height zero into confirmed custody or anchors collateral.
        Assert.Equal(0, repository.GetConfirmedBalance(1_000).Satoshi);
        Assert.Equal(0, repository.GetBalanceWithConfirmations(1_000, 1).Satoshi);
        Assert.Equal(0, repository.GetAvailableConfirmedBalance(1_000, new HashSet<(Domain.Bitcoin.ValueObjects.TxId, uint)>()).Satoshi);
        Assert.Empty(repository.GetUnreservedUtxos());
        var reservation = Guid.NewGuid();
        Assert.True(repository.TryReserveForFee([(mined.TxId, mined.Index)], reservation));
        repository.RemoveUnconfirmed(mined.TxId, mined.Index);
        Assert.False(repository.TryGetUtxo(mined.TxId, mined.Index, out _));
        Assert.True(repository.TryGetFeeReservation(mined.TxId, mined.Index, out var retained));
        Assert.Equal(reservation, retained);
        // Confirmation upgrades custody exactly once without dropping the lease; rewind makes it transient again.
        repository.AddUnconfirmed(transient);
        repository.Add(mined);
        Assert.Empty(repository.GetUnconfirmedUtxos());
        Assert.Equal(mined.Amount, repository.GetConfirmedBalance(1_000));
        repository.Spend(mined);
        repository.AddUnconfirmed(transient);
        Assert.Equal(0, repository.GetConfirmedBalance(1_000).Satoshi);
        Assert.True(repository.TryGetFeeReservation(mined.TxId, mined.Index, out retained));
        Assert.Equal(reservation, retained);
        Assert.False(repository.TryReserveForFee([(mined.TxId, mined.Index)], Guid.NewGuid()));
    }

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