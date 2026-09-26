using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Constants;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Constants;
using Infrastructure.Bitcoin.Wallet;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// <see cref="FeeInputSelector"/> (BOLT 5 plan O7-T1) over an in-memory UTXO set and a mocked unit of work; the
/// database round trip, the restart and concurrency on a real database are in
/// <c>Integration.Tests/Persistence/FeeInputReservationPersistenceTests</c>.
/// </summary>
public class FeeInputSelectorTests
{
    private static readonly LightningMoney s_feeRate = LightningMoney.Satoshis(1_000);

    private readonly FakeWalletUtxoRepository _utxos = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IFeeInputReservationDbRepository> _reservations = new();
    private readonly Mock<IBroadcastTransactionDbRepository> _broadcasts = new();
    private readonly Mock<IBitcoinWalletService> _walletService = new();
    private readonly List<FeeInputReservation> _added = [];
    private readonly BitcoinAddress _changeAddress = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit,
                                                                                 Network.RegTest);
    private readonly FeeInputSelector _selector;

    public FeeInputSelectorTests()
    {
        _unitOfWork.Setup(u => u.FeeInputReservationDbRepository).Returns(_reservations.Object);
        _unitOfWork.Setup(u => u.BroadcastTransactionDbRepository).Returns(_broadcasts.Object);
        _broadcasts.Setup(b => b.GetPendingAsync()).ReturnsAsync([]);
        _unitOfWork.Setup(u => u.SaveChangesAsync()).Returns(Task.CompletedTask);
        _reservations.Setup(r => r.Add(It.IsAny<FeeInputReservation>(), It.IsAny<DateTimeOffset>()))
                     .Callback<FeeInputReservation, DateTimeOffset>((r, _) =>
                      {
                          lock (_added)
                              _added.Add(r);
                      });
        _walletService.Setup(w => w.GetUnusedAddressAsync(AddressType.P2Wpkh, true))
                      .ReturnsAsync(new WalletAddressModel(AddressType.P2Wpkh, 0, true, _changeAddress.ToString()));

        var services = new ServiceCollection();
        services.AddScoped(_ => _unitOfWork.Object);
        services.AddScoped(_ => _walletService.Object);
        var provider = services.BuildServiceProvider();

        var nodeOptions = new NodeOptions { BitcoinNetwork = NetworkConstants.Regtest };
        _selector = new FeeInputSelector(_utxos, provider.GetRequiredService<IServiceScopeFactory>(),
                                         Microsoft.Extensions.Options.Options.Create(nodeOptions),
                                         NullLogger<FeeInputSelector>.Instance);
    }

    [Fact]
    public async Task Given_OneLargeOutput_When_Reserving_Then_ItPaysTheFeeAndTheRestIsChange()
    {
        // Arrange
        var utxo = AddUtxo(100_000);

        // Act
        var reservation = await _selector.ReserveAsync(LightningMoney.Satoshis(1_000), s_feeRate, 500, "cpfp:test",
                                                       TestContext.Current.CancellationToken);

        // Assert: 1,000 + 1,000 sat/kw x (500 + 272 + 124) weight
        var input = Assert.Single(reservation.Inputs);
        Assert.Equal(utxo.TxId, input.TxId);
        Assert.Equal(WalletWeights.P2WpkhInputWeight, input.InputWeight);
        Assert.Equal(1_896, reservation.Fee.Satoshi);
        Assert.Equal(100_000 - 1_896, reservation.ChangeAmount.Satoshi);
        Assert.Equal(_changeAddress.ScriptPubKey.ToBytes(), (byte[])reservation.ChangeScript!.Value);
        Assert.Equal(GetScript(utxo), (byte[])input.ScriptPubKey);
        Assert.True(_utxos.TryGetFeeReservation(utxo.TxId, utxo.Index, out var id));
        Assert.Equal(reservation.Id, id);
        Assert.Same(reservation, Assert.Single(_added));
        _unitOfWork.Verify(u => u.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task Given_ExcessBelowTheDustLimitAfterChange_When_Reserving_Then_NoChangeAndTheExcessIsFee()
    {
        // Arrange: without change the fee is 1,000 + 772 = 1,772; with change 1,896 + 294 dust = 2,190 would be needed
        AddUtxo(2_000);

        // Act
        var reservation = await _selector.ReserveAsync(LightningMoney.Satoshis(1_000), s_feeRate, 500, "cpfp:test",
                                                       TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(reservation.ChangeScript);
        Assert.Equal(0, reservation.ChangeAmount.Satoshi);
        Assert.Equal(2_000, reservation.Fee.Satoshi);
        _walletService.Verify(w => w.GetUnusedAddressAsync(It.IsAny<AddressType>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Given_SmallOutputs_When_Reserving_Then_LargestFirstUntilCovered()
    {
        // Arrange
        AddUtxo(3_000, seed: 1);
        var largest = AddUtxo(9_000, seed: 2);
        var middle = AddUtxo(6_000, seed: 3);

        // Act: 12,000 + 1,000 x (400 + 2 x 272 + 124) / 1000 = 13,068 plus dust needs the two largest
        var reservation = await _selector.ReserveAsync(LightningMoney.Satoshis(12_000), s_feeRate, 400, "htlc:test",
                                                       TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(new[] { largest.TxId, middle.TxId }, reservation.Inputs.Select(i => i.TxId));
        Assert.Equal(15_000, reservation.Total.Satoshi);
        Assert.Equal(13_068, reservation.Fee.Satoshi);
        Assert.Equal(1_932, reservation.ChangeAmount.Satoshi);
    }

    [Fact]
    public async Task Given_TooLittle_When_Reserving_Then_InsufficientFundsAndNothingReserved()
    {
        // Arrange
        var utxo = AddUtxo(1_000);

        // Act / Assert
        await Assert.ThrowsAsync<InsufficientFundsException>(
            () => _selector.ReserveAsync(LightningMoney.Satoshis(5_000), s_feeRate, 500, "cpfp:test",
                                         TestContext.Current.CancellationToken));
        Assert.False(_utxos.TryGetFeeReservation(utxo.TxId, utxo.Index, out _));
        _unitOfWork.Verify(u => u.SaveChangesAsync(), Times.Never);
    }

    [Fact]
    public async Task Given_ReservedLockedUnminedOrAddresslessOutputs_When_Reserving_Then_TheyAreNeverPicked()
    {
        // Arrange
        var reserved = AddUtxo(90_000, seed: 1);
        Assert.True(_utxos.TryReserveForFee([(reserved.TxId, reserved.Index)], Guid.NewGuid()));
        var locked = AddUtxo(80_000, seed: 2);
        locked.LockedToChannelId = ChannelId.Zero;
        _utxos.Add(new UtxoModel(CreateTxId(3), 0, LightningMoney.Satoshis(70_000), 0, CreateAddress(3)));
        _utxos.Add(new UtxoModel(CreateTxId(4), 0, LightningMoney.Satoshis(60_000), 100, 4, false,
                                 AddressType.P2Wpkh));
        var usable = AddUtxo(10_000, seed: 5);

        // Act
        var reservation = await _selector.ReserveAsync(LightningMoney.Satoshis(1_000), s_feeRate, 0, "cpfp:test",
                                                       TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(usable.TxId, Assert.Single(reservation.Inputs).TxId);
    }

    [Fact]
    public async Task Given_AnOutputLockedWhileSelecting_When_Reserving_Then_SelectsAgainWithoutIt()
    {
        // Arrange: a channel funding locks the largest output between the selection and the reservation
        var largest = AddUtxo(90_000, seed: 1);
        var other = AddUtxo(50_000, seed: 2);
        var locks = 0;
        _utxos.BeforeReserve = () =>
        {
            if (locks++ == 0)
                largest.LockedToChannelId = ChannelId.Zero;
        };

        // Act
        var reservation = await _selector.ReserveAsync(LightningMoney.Satoshis(1_000), s_feeRate, 0, "cpfp:test",
                                                       TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(other.TxId, Assert.Single(reservation.Inputs).TxId);
        Assert.Equal(2, locks);
    }

    [Fact]
    public async Task Given_TheSaveFails_When_Reserving_Then_TheOutputsAreFreeAgain()
    {
        // Arrange
        var utxo = AddUtxo(90_000);
        _unitOfWork.Setup(u => u.SaveChangesAsync()).ThrowsAsync(new InvalidOperationException("disk full"));

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _selector.ReserveAsync(LightningMoney.Satoshis(1_000), s_feeRate, 0, "cpfp:test",
                                         TestContext.Current.CancellationToken));
        Assert.False(_utxos.TryGetFeeReservation(utxo.TxId, utxo.Index, out _));
    }

    [Fact]
    public async Task Given_AReservation_When_Released_Then_RowDeletedAndOutputsFree()
    {
        // Arrange
        var utxo = AddUtxo(90_000);
        var reservation = await _selector.ReserveAsync(LightningMoney.Satoshis(1_000), s_feeRate, 0, "cpfp:test",
                                                       TestContext.Current.CancellationToken);
        _reservations.Setup(r => r.DeleteAsync(reservation.Id)).ReturnsAsync(true);

        // Act
        await _selector.ReleaseAsync(reservation.Id, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(_utxos.TryGetFeeReservation(utxo.TxId, utxo.Index, out _));
        _reservations.Verify(r => r.DeleteAsync(reservation.Id), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(), Times.Exactly(2));
    }

    [Fact]
    public async Task Given_AReservationWhoseInputsTheChainMonitorSpent_When_Confirmed_Then_OnlyTheRowIsDeleted()
    {
        // Arrange
        var utxo = AddUtxo(90_000);
        var reservation = await _selector.ReserveAsync(LightningMoney.Satoshis(1_000), s_feeRate, 0, "cpfp:test",
                                                       TestContext.Current.CancellationToken);
        _reservations.Setup(r => r.GetByIdAsync(reservation.Id)).ReturnsAsync(reservation);
        _reservations.Setup(r => r.DeleteAsync(reservation.Id)).ReturnsAsync(true);
        _utxos.Spend(utxo); // the chain monitor processed the block that spends it

        // Act
        var confirmed = await _selector.ConfirmAsync(reservation.Id, TestContext.Current.CancellationToken);

        // Assert: wallet outputs are the chain monitor's to remove (no second delete of the same rows)
        Assert.True(confirmed);
        _unitOfWork.Verify(u => u.TrySpendUtxo(It.IsAny<TxId>(), It.IsAny<uint>()), Times.Never);
        _reservations.Verify(r => r.DeleteAsync(reservation.Id), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(), Times.Exactly(2));
        Assert.False(_utxos.TryGetFeeReservation(utxo.TxId, utxo.Index, out _));
    }

    [Fact]
    public async Task Given_AReservationWithAnInputStillInTheWallet_When_Confirmed_Then_RefusesAndKeepsIt()
    {
        // Arrange: a caller that confirms too early, or the wrong reservation after an RBF that used other inputs
        var utxo = AddUtxo(90_000);
        var reservation = await _selector.ReserveAsync(LightningMoney.Satoshis(1_000), s_feeRate, 0, "cpfp:test",
                                                       TestContext.Current.CancellationToken);
        _reservations.Setup(r => r.GetByIdAsync(reservation.Id)).ReturnsAsync(reservation);

        // Act
        var confirmed = await _selector.ConfirmAsync(reservation.Id, TestContext.Current.CancellationToken);

        // Assert: the coin stays in the wallet and reserved
        Assert.False(confirmed);
        Assert.True(_utxos.TryGetUtxo(utxo.TxId, utxo.Index, out _));
        Assert.True(_utxos.TryGetFeeReservation(utxo.TxId, utxo.Index, out var id));
        Assert.Equal(reservation.Id, id);
        _unitOfWork.Verify(u => u.TrySpendUtxo(It.IsAny<TxId>(), It.IsAny<uint>()), Times.Never);
        _reservations.Verify(r => r.DeleteAsync(It.IsAny<Guid>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task Given_APendingBroadcastSpendingTheLargestOutput_When_Reserving_Then_ItIsNotPicked()
    {
        // Arrange: after a restart our unconfirmed funding transaction's inputs carry no channel lock; only its
        // persisted broadcast row says they are spent
        var funded = AddUtxo(500_000, seed: 1);
        var free = AddUtxo(50_000, seed: 2);
        _broadcasts.Setup(b => b.GetPendingAsync()).ReturnsAsync([CreateBroadcastSpending(funded)]);

        // Act
        var reservation = await _selector.ReserveAsync(LightningMoney.Satoshis(1_000), s_feeRate, 500, "cpfp:test",
                                                       TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(free.TxId, Assert.Single(reservation.Inputs).TxId);
        Assert.False(_utxos.TryGetFeeReservation(funded.TxId, funded.Index, out _));
    }

    [Fact]
    public async Task Given_OnlyOutputsSpentByPendingBroadcasts_When_Reserving_Then_InsufficientFunds()
    {
        // Arrange
        var funded = AddUtxo(500_000);
        _broadcasts.Setup(b => b.GetPendingAsync()).ReturnsAsync([CreateBroadcastSpending(funded)]);

        // Act / Assert
        await Assert.ThrowsAsync<InsufficientFundsException>(
            () => _selector.ReserveAsync(LightningMoney.Satoshis(1_000), s_feeRate, 0, "cpfp:test",
                                         TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_ConcurrentReservations_When_Reserving_Then_NoOutputIsReservedTwice()
    {
        // Arrange
        for (byte seed = 1; seed <= 20; seed++)
            AddUtxo(10_000 + seed, seed);

        // Act
        var reservations = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(
            () => _selector.ReserveAsync(LightningMoney.Satoshis(1_000), s_feeRate, 0, $"cpfp:{i}",
                                         TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)));

        // Assert
        var outpoints = reservations.SelectMany(r => r.Inputs).Select(i => (i.TxId, i.Index)).ToList();
        Assert.Equal(20, outpoints.Count);
        Assert.Equal(20, outpoints.Distinct().Count());
        await Assert.ThrowsAsync<InsufficientFundsException>(
            () => _selector.ReserveAsync(LightningMoney.Satoshis(1_000), s_feeRate, 0, "cpfp:late",
                                         TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Given_NoPurpose_When_Reserving_Then_Throws(string purpose)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _selector.ReserveAsync(LightningMoney.Satoshis(1_000), s_feeRate, 0, purpose,
                                         TestContext.Current.CancellationToken));
    }

    private UtxoModel AddUtxo(long amountSat, byte seed = 1)
    {
        var utxo = new UtxoModel(CreateTxId(seed), 0, LightningMoney.Satoshis(amountSat), 100, CreateAddress(seed));
        _utxos.Add(utxo);
        return utxo;
    }

    private static BroadcastTransactionModel CreateBroadcastSpending(UtxoModel utxo)
    {
        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new TxIn(new OutPoint(new uint256((byte[])utxo.TxId), utxo.Index)));
        tx.Outputs.Add(Money.Satoshis(utxo.Amount.Satoshi - 1_000), new Key().PubKey.WitHash.ScriptPubKey);
        return new BroadcastTransactionModel(new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes()),
                                             BroadcastPurpose.Funding, null, 100);
    }

    private static TxId CreateTxId(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());

    private static WalletAddressModel CreateAddress(byte seed)
    {
        var key = new Key(Enumerable.Repeat(seed, 32).ToArray());
        return new WalletAddressModel(AddressType.P2Wpkh, seed, false,
                                      key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest).ToString());
    }

    private static byte[] GetScript(UtxoModel utxo) =>
        BitcoinAddress.Create(utxo.WalletAddress!.Address, Network.RegTest).ScriptPubKey.ToBytes();
}