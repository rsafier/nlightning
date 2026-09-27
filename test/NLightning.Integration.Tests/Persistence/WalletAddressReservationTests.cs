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
using Infrastructure.Repositories;
using Infrastructure.Repositories.Memory;

/// <summary>
/// NL-045: an address reserved for a channel's <c>upfront_shutdown_script</c> is saved as reserved and never handed out
/// by the unused-address lookup again, also from a new unit of work (a restart), over the real SQLite schema.
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

        // Assert: the plain lookup still returns its first free address to every caller until funds arrive (NL-280),
        // but a reserved one is never returned again
        Assert.True(reserved.IsReserved);
        Assert.Equal(0u, reserved.Index);
        Assert.Equal(1u, unused.Index);
        Assert.Equal(1u, secondReserved.Index);
        Assert.Equal(2u, afterRestart.Index);
        Assert.False(afterRestart.IsReserved);

        using var check = CreateUnitOfWork(database, utxos);
        var stored = check.WalletAddressesDbRepository.GetAllAddresses()
                          .Where(a => a is { AddressType: AddressType.P2Wpkh, IsChange: false })
                          .ToDictionary(a => a.Index);
        Assert.True(stored[0].IsReserved);
        Assert.True(stored[1].IsReserved);
        Assert.False(stored[2].IsReserved);
        Assert.Equal(reserved.Address, stored[0].Address);
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