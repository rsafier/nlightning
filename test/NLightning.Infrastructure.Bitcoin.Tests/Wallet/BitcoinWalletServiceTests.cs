using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Bitcoin.Wallet;
using Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.ValueObjects;

public class BitcoinWalletServiceTests
{
    private static readonly ExtKey s_masterKey =
        ExtKey.CreateFromSeed(Convert.FromHexString("000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f"));

    private readonly Mock<IBlockchainMonitor> _blockchainMonitor = new();
    private readonly Mock<ISecureKeyManager> _secureKeyManager = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IWalletAddressesDbRepository> _addresses = new();
    private readonly List<WalletAddressModel> _stored = [];

    public BitcoinWalletServiceTests()
    {
        _secureKeyManager.Setup(k => k.GetDepositP2WpkhKeyAtIndex(It.IsAny<uint>(), It.IsAny<bool>()))
                         .Returns((uint index, bool isChange) =>
                                      s_masterKey.Derive(isChange ? 1u : 0u).Derive(index).ToBytes());
        _secureKeyManager.Setup(k => k.GetDepositP2TrKeyAtIndex(It.IsAny<uint>(), It.IsAny<bool>()))
                         .Returns((uint index, bool isChange) =>
                                      s_masterKey.Derive(isChange ? 3u : 2u).Derive(index).ToBytes());

        // An in-memory table with the repository's semantics: no UTXO rows, highest index (0 when empty)
        _addresses.Setup(r => r.GetUnusedAddressAsync(It.IsAny<AddressType>(), It.IsAny<bool>()))
                  .ReturnsAsync((WalletAddressModel?)null);
        _addresses.Setup(r => r.GetLastUsedAddressIndex(It.IsAny<AddressType>(), It.IsAny<bool>()))
                  .ReturnsAsync((AddressType type, bool isChange) =>
                                    _stored.Where(a => a.AddressType == type && a.IsChange == isChange)
                                           .Select(a => a.Index)
                                           .DefaultIfEmpty(0u)
                                           .Max());
        _addresses.Setup(r => r.GetAllAddresses()).Returns(() => _stored.ToList());
        _addresses.Setup(r => r.AddRange(It.IsAny<List<WalletAddressModel>>()))
                  .Callback((List<WalletAddressModel> batch) =>
                   {
                       // The table's key is (Index, IsChange, AddressType): a duplicate fails the save
                       foreach (var address in batch)
                       {
                           if (_stored.Any(a => a.Index == address.Index && a.IsChange == address.IsChange
                                             && a.AddressType == address.AddressType))
                               throw new InvalidOperationException($"Duplicate wallet address index {address.Index}");

                           _stored.Add(address);
                       }
                   });
        _unitOfWork.Setup(u => u.WalletAddressesDbRepository).Returns(_addresses.Object);
    }

    [Fact]
    public async Task Given_EmptyWallet_When_GettingAnAddress_Then_TheFirstBatchStartsAtIndexZero()
    {
        // Arrange
        var service = CreateService(BitcoinNetwork.Regtest);

        // Act
        var address = await service.GetUnusedAddressAsync(AddressType.P2Wpkh, false);

        // Assert
        Assert.Equal(0u, address.Index);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => (uint)i), _stored.Select(a => a.Index));
    }

    [Fact]
    public async Task Given_TenAddressesUsed_When_GettingANewBatch_Then_ItStartsAfterTheHighestIndex()
    {
        // Arrange: the first batch (0-9) is all used, so the repository finds no unused address (NL-283)
        var service = CreateService(BitcoinNetwork.Regtest);
        await service.GetUnusedAddressAsync(AddressType.P2Wpkh, false);

        // Act
        var address = await service.GetUnusedAddressAsync(AddressType.P2Wpkh, false);

        // Assert: 10-19, no index 9 again (the duplicate key failed the save before the fix)
        Assert.Equal(10u, address.Index);
        Assert.Equal(20, _stored.Count);
        Assert.Equal(Enumerable.Range(0, 20).Select(i => (uint)i), _stored.Select(a => a.Index));
        Assert.Equal(20, _stored.Select(a => a.Address).Distinct().Count());
    }

    [Fact]
    public async Task Given_OnlyIndexZeroStored_When_GettingANewBatch_Then_ItStartsAtIndexOne()
    {
        // Arrange: the repository reports 0 for "only index 0" as for "empty"
        _stored.Add(new WalletAddressModel(AddressType.P2Wpkh, 0, false, "existing"));
        var service = CreateService(BitcoinNetwork.Regtest);

        // Act
        var address = await service.GetUnusedAddressAsync(AddressType.P2Wpkh, false);

        // Assert
        Assert.Equal(1u, address.Index);
        Assert.Equal(11, _stored.Count);
    }

    [Fact]
    public async Task Given_OtherChainHasAddresses_When_GettingAChangeAddress_Then_ChangeStartsAtZero()
    {
        // Arrange
        var service = CreateService(BitcoinNetwork.Regtest);
        await service.GetUnusedAddressAsync(AddressType.P2Wpkh, false);

        // Act
        var change = await service.GetUnusedAddressAsync(AddressType.P2Wpkh, true);

        // Assert
        Assert.Equal(0u, change.Index);
        Assert.True(change.IsChange);
    }

    [Theory]
    [InlineData("signet", AddressType.P2Wpkh, "tb1q")]
    [InlineData("signet", AddressType.P2Tr, "tb1p")]
    [InlineData("mutinynet", AddressType.P2Wpkh, "tb1q")]
    [InlineData("regtest", AddressType.P2Wpkh, "bcrt1q")]
    [InlineData("mainnet", AddressType.P2Wpkh, "bc1q")]
    public async Task Given_Network_When_GettingAnAddress_Then_ItIsEncodedForThatNetwork(string network,
        AddressType addressType, string prefix)
    {
        // Arrange
        var service = CreateService(new BitcoinNetwork(network));

        // Act
        var address = await service.GetUnusedAddressAsync(addressType, false);

        // Assert
        Assert.StartsWith(prefix, address.Address);
    }

    [Fact]
    public void Given_UnknownNetwork_When_Constructed_Then_ItThrowsInsteadOfUsingMainnet()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => CreateService(new BitcoinNetwork("unknown-net")));
    }

    private BitcoinWalletService CreateService(BitcoinNetwork network)
    {
        return new BitcoinWalletService(_blockchainMonitor.Object, NullLogger<BitcoinWalletService>.Instance,
                                        new OptionsWrapper<NodeOptions>(new NodeOptions { BitcoinNetwork = network }),
                                        _secureKeyManager.Object, _unitOfWork.Object);
    }
}