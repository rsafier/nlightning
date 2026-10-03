using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Wallet.Models;
using Domain.Crypto.Hashes;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Wallet;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Providers;
using Infrastructure.Repositories;
using Infrastructure.Repositories.Memory;

/// <summary>
/// NL-045 and NL-280: every address the wallet hands out (a channel's <c>upfront_shutdown_script</c>, a shutdown, sweep
/// or change address) is saved as reserved and never handed out by the unused-address lookup again, also from a new
/// unit of work (a restart) and after its funds were spent, over the real SQLite schema.
/// </summary>
public class WalletAddressReservationTests
{
    private static readonly ExtKey s_masterKey =
        ExtKey.CreateFromSeed(Convert.FromHexString("000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f"));

    [Fact]
    public async Task Given_AReservedAddress_When_UnusedAddressesAreAskedFor_Then_ItIsNeverReturnedAgain()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        var utxos = new UtxoMemoryRepository();

        // Act
        WalletAddressModel reserved;
        WalletAddressModel secondReserved;
        WalletAddressModel unused;
        using (var uow = CreateUnitOfWork(database, utxos))
        {
            var wallet = CreateWallet(uow);
            reserved = await wallet.ReserveUnusedAddressAsync(AddressType.P2Wpkh, false);
            unused = await wallet.GetUnusedAddressAsync(AddressType.P2Wpkh, false);
            secondReserved = await wallet.ReserveUnusedAddressAsync(AddressType.P2Wpkh, false);
        }

        WalletAddressModel afterRestart;
        using (var uow = CreateUnitOfWork(database, utxos))
            afterRestart = await CreateWallet(uow).GetUnusedAddressAsync(AddressType.P2Wpkh, false);

        // Assert: every address handed out is reserved (NL-280), by either call, and never returned again
        Assert.True(reserved.IsReserved);
        Assert.True(unused.IsReserved);
        Assert.Equal(0u, reserved.Index);
        Assert.Equal(1u, unused.Index);
        Assert.Equal(2u, secondReserved.Index);
        Assert.Equal(3u, afterRestart.Index);

        using var check = CreateUnitOfWork(database, utxos);
        var stored = check.WalletAddressesDbRepository.GetAllAddresses()
                          .Where(a => a is { AddressType: AddressType.P2Wpkh, IsChange: false })
                          .ToDictionary(a => a.Index);
        Assert.True(stored[0].IsReserved);
        Assert.True(stored[1].IsReserved);
        Assert.True(stored[2].IsReserved);
        Assert.True(stored[3].IsReserved);
        Assert.False(stored[4].IsReserved);
        Assert.Equal(reserved.Address, stored[0].Address);
    }

    [Fact]
    public async Task Given_TwoCallersAskingForAChangeAddress_When_NoFundsArrivedYet_Then_EachGetsItsOwnAddress()
    {
        // Arrange: FeeInputSelector takes its change script from here; two reservations used to share one (NL-280)
        using var database = new SqliteTestDatabase();
        var utxos = new UtxoMemoryRepository();

        // Act
        WalletAddressModel first;
        WalletAddressModel second;
        using (var uow = CreateUnitOfWork(database, utxos))
            first = await CreateWallet(uow).GetUnusedAddressAsync(AddressType.P2Wpkh, true);
        using (var uow = CreateUnitOfWork(database, utxos))
            second = await CreateWallet(uow).GetUnusedAddressAsync(AddressType.P2Wpkh, true);

        // Assert
        Assert.NotEqual(first.Address, second.Address);
        Assert.True(first.IsChange);
        Assert.True(second.IsChange);
    }

    [Fact]
    public async Task Given_AnAddressWhoseFundsWereSpent_When_AnUnusedAddressIsAskedFor_Then_ItIsNotHandedOutAgain()
    {
        // Arrange: the address is handed out, funded, and its UTXO spent (the row is deleted on spend)
        using var database = new SqliteTestDatabase();
        var utxos = new UtxoMemoryRepository();
        WalletAddressModel deposit;
        using (var uow = CreateUnitOfWork(database, utxos))
            deposit = await CreateWallet(uow).GetUnusedAddressAsync(AddressType.P2Wpkh, false);

        var utxo = SqliteTestDatabase.CreateUtxo(deposit);
        using (var uow = CreateUnitOfWork(database, utxos))
        {
            uow.AddUtxo(utxo);
            await uow.SaveChangesAsync();
        }

        using (var uow = CreateUnitOfWork(database, utxos))
        {
            uow.TrySpendUtxo(utxo.TxId, utxo.Index);
            await uow.SaveChangesAsync();
        }

        // Act
        WalletAddressModel next;
        using (var uow = CreateUnitOfWork(database, utxos))
            next = await CreateWallet(uow).GetUnusedAddressAsync(AddressType.P2Wpkh, false);

        // Assert
        Assert.NotEqual(deposit.Address, next.Address);
        Assert.Equal(deposit.Index + 1, next.Index);
    }

    [Fact]
    public async Task Given_LegacyAddressesNotReserved_When_ALaterOneHoldsFunds_Then_TheLookupNeverGoesBackBelowIt()
    {
        // Arrange: rows from before reservations: index 0 was handed out and spent (no UTXO row left, not reserved),
        // index 1 still holds a UTXO
        using var database = new SqliteTestDatabase();
        var utxos = new UtxoMemoryRepository();
        using (var uow = CreateUnitOfWork(database, utxos))
        {
            uow.WalletAddressesDbRepository.AddRange([
                SqliteTestDatabase.CreateWalletAddress(0), SqliteTestDatabase.CreateWalletAddress(1),
                SqliteTestDatabase.CreateWalletAddress(2), SqliteTestDatabase.CreateWalletAddress(3)
            ]);
            await uow.SaveChangesAsync();
            uow.AddUtxo(SqliteTestDatabase.CreateUtxo(SqliteTestDatabase.CreateWalletAddress(1)));
            await uow.SaveChangesAsync();
        }

        // Act
        WalletAddressModel? next;
        using (var uow = CreateUnitOfWork(database, utxos))
            next = await uow.WalletAddressesDbRepository.GetUnusedAddressAsync(AddressType.P2Wpkh, false);

        // Assert: index 0 (spent) is skipped
        Assert.NotNull(next);
        Assert.Equal(2u, next.Index);
    }

    [Fact]
    public async Task Given_TheSqliteSchema_When_AddressesAreIssuedAndBroadcastsAbandoned_Then_TheSharedRoundTripHolds()
    {
        // Arrange (the SQLite run of the round trip the Docker Postgres/SQL Server tests share)
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseSqlite(connection, x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                     .Options;

        // Act & Assert
        await WalletIssuanceSchemaRoundTrip.AssertAsync(
            () => new NLightningDbContext(options, new DatabaseTypeProvider(DatabaseType.Sqlite)),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_AnAddressThatIsNotStored_When_Reserved_Then_TheRepositoryThrows()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        using var uow = CreateUnitOfWork(database, new UtxoMemoryRepository());

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => uow.WalletAddressesDbRepository.ReserveAsync(SqliteTestDatabase.CreateWalletAddress(42)));
    }

    private static BitcoinWalletService CreateWallet(UnitOfWork uow)
    {
        var keyManager = new Mock<ISecureKeyManager>();
        keyManager.Setup(k => k.GetDepositP2WpkhKeyAtIndex(It.IsAny<uint>(), It.IsAny<bool>()))
                  .Returns((uint index, bool isChange) => s_masterKey.Derive(isChange ? 1u : 0u).Derive(index).ToBytes());
        return new BitcoinWalletService(new Mock<IBlockchainMonitor>().Object,
                                        NullLogger<BitcoinWalletService>.Instance,
                                        Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }),
                                        keyManager.Object, uow);
    }

    private static UnitOfWork CreateUnitOfWork(SqliteTestDatabase database, UtxoMemoryRepository utxos) =>
        new(database.CreateContext(), new Mock<ILogger<UnitOfWork>>().Object, new Mock<ISha256>().Object, utxos);
}