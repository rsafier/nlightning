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

public partial class BitcoinWalletServiceTests
{
    private static readonly ExtKey s_masterKey =
        ExtKey.CreateFromSeed(Convert.FromHexString("000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f"));

    private readonly Mock<IBlockchainMonitor> _blockchainMonitor = new();
    private readonly Mock<ISecureKeyManager> _secureKeyManager = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IWalletAddressesDbRepository> _addresses = new();
    private readonly List<WalletAddressModel> _stored = [];
    private readonly HashSet<uint> _funded = [];
    private readonly HashSet<(AddressType Type, bool IsChange, uint Index)> _reserved = [];

    public BitcoinWalletServiceTests()
    {
        _secureKeyManager.Setup(k => k.GetWalletPublicKey(It.IsAny<uint>(), It.IsAny<bool>(), It.IsAny<AddressType>()))
                         .Returns((uint index, bool isChange, AddressType type) =>
                                      (Domain.Crypto.ValueObjects.CompactPubKey)s_masterKey
                                          .Derive(type == AddressType.P2Tr ? (isChange ? 3u : 2u) : (isChange ? 1u : 0u))
                                          .Derive(index).Neuter().PubKey.ToBytes());

        // The repository's semantics: the lowest stored address that is not reserved, holds no UTXO and lies above
        // every reserved or funded one (NL-280); an empty table gives null
        _addresses.Setup(r => r.GetUnusedAddressAsync(It.IsAny<AddressType>(), It.IsAny<bool>()))
                  .ReturnsAsync((AddressType type, bool isChange) =>
                  {
                      var mine = _stored.Where(a => a.AddressType == type && a.IsChange == isChange).ToList();
                      var highestUsed = mine.Where(a => _reserved.Contains((type, isChange, a.Index))
                                                     || _funded.Contains(a.Index))
                                            .Select(a => (long)a.Index)
                                            .DefaultIfEmpty(-1)
                                            .Max();
                      return mine.Where(a => !_reserved.Contains((type, isChange, a.Index))
                                          && !_funded.Contains(a.Index) && a.Index > highestUsed)
                                 .OrderBy(a => a.Index)
                                 .Cast<WalletAddressModel?>()
                                 .FirstOrDefault();
                  });
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
        _addresses.Setup(r => r.ReserveAsync(It.IsAny<WalletAddressModel>()))
                  .Callback((WalletAddressModel address) =>
                   {
                       var stored = _stored.FirstOrDefault(a => a.Index == address.Index
                                                             && a.IsChange == address.IsChange
                                                             && a.AddressType == address.AddressType)
                                    ?? throw new InvalidOperationException(
                                          $"Wallet address {address.Address} is not stored");
                       _reserved.Add((address.AddressType, address.IsChange, address.Index));
                   });
        _unitOfWork.Setup(u => u.WalletAddressesDbRepository).Returns(_addresses.Object);
    }

    [Fact]
    public async Task Given_EmptyWallet_When_GettingAnAddress_Then_TheFirstBatchStartsAtIndexZeroWithAWindow()
    {
        // Arrange
        var service = CreateService(BitcoinNetwork.Regtest);

        // Act
        var address = await service.GetUnusedAddressAsync(AddressType.P2Wpkh, false);

        // Assert: the hand-out batch plus the discovery window (NL-463), all watched, only the hand-out reserved
        Assert.Equal(0u, address.Index);
        Assert.Equal(Enumerable.Range(0, 10 + BitcoinWalletService.GapLimit).Select(i => (uint)i),
                     _stored.Select(a => a.Index));
        _blockchainMonitor.Verify(m => m.WatchBitcoinAddress(It.IsAny<WalletAddressModel>()),
                                  Times.Exactly(10 + BitcoinWalletService.GapLimit));
        Assert.Single(_reserved);
    }

    [Fact]
    public async Task Given_AFreshBatch_When_MoreAddressesAreNeeded_Then_TheStoredOnesAreUsedBeforeANewBatch()
    {
        // Arrange
        var service = CreateService(BitcoinNetwork.Regtest);
        await service.GetUnusedAddressAsync(AddressType.P2Wpkh, false);

        // Act
        var addresses = new List<WalletAddressModel>();
        for (var i = 0; i < 12; i++)
            addresses.Add(await service.GetUnusedAddressAsync(AddressType.P2Wpkh, false));

        // Assert: indexes 1..12, all reserved, no second batch generated (30 stored in total)
        Assert.Equal(Enumerable.Range(1, 12).Select(i => (uint)i), addresses.Select(a => a.Index));
        Assert.Equal(10 + BitcoinWalletService.GapLimit, _stored.Count);
    }

    [Fact]
    public async Task Given_TheWholeWindowUsed_When_GettingANewBatch_Then_ItContinuesWithAWindow()
    {
        // Arrange: indexes 0..29 are all handed out (reserved)
        var service = CreateService(BitcoinNetwork.Regtest);
        for (var i = 0; i < 10 + BitcoinWalletService.GapLimit; i++)
            await service.GetUnusedAddressAsync(AddressType.P2Wpkh, false);

        // Act
        var address = await service.GetUnusedAddressAsync(AddressType.P2Wpkh, false);

        // Assert: the next batch runs 30..59, none of the old ones again (the duplicate key failed the save before
        // the fix)
        Assert.Equal(30u, address.Index);
        Assert.Equal(2 * (10 + BitcoinWalletService.GapLimit), _stored.Count);
        Assert.Equal(Enumerable.Range(0, 2 * (10 + BitcoinWalletService.GapLimit)).Select(i => (uint)i),
                     _stored.Select(a => a.Index));
        Assert.Equal(_stored.Count, _stored.Select(a => a.Address).Distinct().Count());
    }

    [Fact]
    public async Task Given_AWindowAddressReceivedFunds_When_AnUnusedAddressIsAskedFor_Then_TheWalletMovesPastIt()
    {
        // Arrange: the batch 0..29 was generated and a deposit (restored funds, NL-463) landed on window address 25
        var service = CreateService(BitcoinNetwork.Regtest);
        await service.GetUnusedAddressAsync(AddressType.P2Wpkh, false);
        _funded.Add(25);

        // Act
        var addresses = new List<WalletAddressModel>();
        for (var i = 0; i < 6; i++)
            addresses.Add(await service.GetUnusedAddressAsync(AddressType.P2Wpkh, false));

        // Assert: nothing below the funded address is handed out again (NL-280); hand-outs continue 26..29, then the
        // pool is exhausted and the next batch keeps a window past it (30..59)
        Assert.Equal([26u, 27u, 28u, 29u, 30u, 31u], addresses.Select(a => a.Index));
        Assert.Equal(2 * (10 + BitcoinWalletService.GapLimit), _stored.Count);
    }

    [Fact]
    public async Task Given_OnlyIndexZeroStored_When_GettingANewBatch_Then_ItStartsAtIndexOne()
    {
        // Arrange: the repository reports 0 for "only index 0" as for "empty" (the address is in use, so the unused
        // lookup finds nothing)
        _stored.Add(new WalletAddressModel(AddressType.P2Wpkh, 0, false, "existing"));
        _funded.Add(0);
        var service = CreateService(BitcoinNetwork.Regtest);

        // Act
        var address = await service.GetUnusedAddressAsync(AddressType.P2Wpkh, false);

        // Assert
        Assert.Equal(1u, address.Index);
        Assert.Equal(1 + 10 + BitcoinWalletService.GapLimit, _stored.Count);
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
        Assert.Equal(10 + BitcoinWalletService.GapLimit,
                     _stored.Count(a => a.AddressType == AddressType.P2Wpkh && a.IsChange));
    }

    [Fact]
    public async Task Given_TwoScopesWithoutUnusedAddresses_When_GettingAddressesConcurrently_Then_BatchesDoNotCollide()
    {
        // Arrange: the first batch is fully used, so the index query yields, and both scopes would read the same
        // highest index without the lock
        var first = CreateService(BitcoinNetwork.Regtest);
        for (var i = 0; i < 10 + BitcoinWalletService.GapLimit; i++)
            await first.GetUnusedAddressAsync(AddressType.P2Wpkh, false);
        // Both scopes find no unused address (the pool ran out between their lookups), and the index query yields
        _addresses.Setup(r => r.GetUnusedAddressAsync(It.IsAny<AddressType>(), It.IsAny<bool>()))
                  .ReturnsAsync((WalletAddressModel?)null);
        _addresses.Setup(r => r.GetLastUsedAddressIndex(It.IsAny<AddressType>(), It.IsAny<bool>()))
                  .Returns(async (AddressType type, bool isChange) =>
                   {
                       var highest = _stored.Where(a => a.AddressType == type && a.IsChange == isChange)
                                            .Select(a => a.Index)
                                            .DefaultIfEmpty(0u)
                                            .Max();
                       await Task.Delay(50, TestContext.Current.CancellationToken);
                       return highest;
                   });
        var second = CreateService(BitcoinNetwork.Regtest);
        var third = CreateService(BitcoinNetwork.Regtest);

        // Act
        var addresses = await Task.WhenAll(second.GetUnusedAddressAsync(AddressType.P2Wpkh, false),
                                           third.GetUnusedAddressAsync(AddressType.P2Wpkh, false));

        // Assert: 30..59 and 60..89, no duplicate key (NL-283)
        Assert.Equal([30u, 60u], addresses.Select(a => a.Index).Order());
        Assert.Equal(Enumerable.Range(0, 3 * (10 + BitcoinWalletService.GapLimit)).Select(i => (uint)i),
                     _stored.Select(a => a.Index).Order());
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