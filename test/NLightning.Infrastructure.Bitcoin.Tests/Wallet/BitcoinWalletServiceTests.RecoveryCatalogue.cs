using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Bitcoin.Wallet;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Wallet.Models;
using Domain.Node.Options;
using Domain.Protocol.ValueObjects;

public partial class BitcoinWalletServiceTests
{
    [Fact]
    public async Task Given_FreshSeed_When_RecoveryCatalogueIsStagedTwice_Then_AllFourChainsAreDerivedWithoutReservations()
    {
        // Arrange
        var source = RecoverySource();
        // Act
        var first = await source.StageAddressesAsync(_unitOfWork.Object, cancellationToken: TestContext.Current.CancellationToken);
        var second = await source.StageAddressesAsync(_unitOfWork.Object, cancellationToken: TestContext.Current.CancellationToken);
        // Assert
        Assert.Equal(120, first.Count);
        Assert.Equal(first.Select(a => a.Address), second.Select(a => a.Address));
        Assert.Equal(120, _stored.Count);
        Assert.Empty(_reserved);
        foreach (var type in new[] { AddressType.P2Wpkh, AddressType.P2Tr })
            foreach (var change in new[] { false, true })
            {
                var branch = type == AddressType.P2Tr ? (change ? 3u : 2u) : (change ? 1u : 0u);
                var expected = s_masterKey.Derive(branch).Derive(29).PrivateKey.PubKey.GetAddress(
                    type == AddressType.P2Tr ? ScriptPubKeyType.TaprootBIP86 : ScriptPubKeyType.Segwit, Network.RegTest).ToString();
                Assert.Equal(expected, Assert.Single(first, a => a.AddressType == type && a.IsChange == change && a.Index == 29).Address);
            }
        _addresses.Verify(r => r.AddRange(It.IsAny<List<WalletAddressModel>>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(), Times.Never);
        _blockchainMonitor.Verify(m => m.WatchBitcoinAddress(It.IsAny<WalletAddressModel>()), Times.Never);
    }

    [Fact]
    public async Task Given_RepositoryExcludesUnsavedRows_When_CatalogueIsExtendedInOneUnitOfWork_Then_NoDuplicateRowsAreStaged()
    {
        // Arrange: EF's no-tracking query cannot see Added entities until the caller commits.
        _addresses.Setup(r => r.GetAllAddresses()).Returns(Array.Empty<WalletAddressModel>());
        var source = RecoverySource();
        // Act
        await source.StageAddressesAsync(_unitOfWork.Object, 30, TestContext.Current.CancellationToken);
        await source.StageAddressesAsync(_unitOfWork.Object, 40, TestContext.Current.CancellationToken);
        await source.StageAddressesAsync(_unitOfWork.Object, 40, TestContext.Current.CancellationToken);
        // Assert
        Assert.Equal(160, _stored.Count);
        _addresses.Verify(r => r.AddRange(It.IsAny<List<WalletAddressModel>>()), Times.Exactly(2));
        _unitOfWork.Verify(u => u.SaveChangesAsync(), Times.Never);
    }

    [Fact]
    public async Task Given_ReservedCatalogue_When_RecoveryExtendsItsBound_Then_ReservationsAndNextAddressArePreserved()
    {
        // Arrange
        var source = RecoverySource();
        await source.StageAddressesAsync(_unitOfWork.Object, cancellationToken: TestContext.Current.CancellationToken);
        var service = CreateService(BitcoinNetwork.Regtest);
        var handedOut = await service.GetUnusedAddressAsync(AddressType.P2Tr, true);
        // Act
        await source.StageAddressesAsync(_unitOfWork.Object, 40, TestContext.Current.CancellationToken);
        var next = await service.GetUnusedAddressAsync(AddressType.P2Tr, true);
        // Assert
        Assert.Equal(0u, handedOut.Index);
        Assert.Equal(1u, next.Index);
        Assert.Equal(160, _stored.Count);
        Assert.Equal(2, _reserved.Count);
    }

    [Fact]
    public async Task Given_WrongSeedCatalogue_When_Staged_Then_RejectsWithoutPartiallyStagingAddresses()
    {
        // Arrange
        _stored.Add(new WalletAddressModel(AddressType.P2Tr, 0, true, "foreign-seed"));
        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => RecoverySource().StageAddressesAsync(
            _unitOfWork.Object, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(_stored);
        _addresses.Verify(r => r.AddRange(It.IsAny<List<WalletAddressModel>>()), Times.Never);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(100_001u)]
    public async Task Given_UnboundedRecoveryCatalogue_When_Staged_Then_RefusedBeforeDerivingKeys(uint count)
    {
        // Act / Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => RecoverySource().StageAddressesAsync(
            _unitOfWork.Object, count, TestContext.Current.CancellationToken));
        Assert.Empty(_stored);
        _secureKeyManager.Verify(k => k.GetDepositP2TrKeyAtIndex(It.IsAny<uint>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Given_CancelledRecovery_When_CatalogueRequested_Then_NoAddressesAreStaged()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        // Act / Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RecoverySource().StageAddressesAsync(
            _unitOfWork.Object, cancellationToken: cancellation.Token));
        Assert.Empty(_stored);
    }

    private SilentPaymentRecoveryAddressSource RecoverySource() => new(_secureKeyManager.Object,
        new OptionsWrapper<NodeOptions>(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }));
}