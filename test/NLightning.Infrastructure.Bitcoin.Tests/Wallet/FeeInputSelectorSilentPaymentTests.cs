using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Money;

public partial class FeeInputSelectorTests
{
    [Fact]
    public async Task Given_LargeSilentCoinAndEnoughOrdinaryCoins_When_Selecting_Then_SilentCoinIsNotLinked()
    {
        // Arrange
        var ordinary = AddUtxo(30_000);
        var silent = AddSilentCoin(100_000, 7);
        // Act
        var reservation = await _selector.ReserveAsync(LightningMoney.Satoshis(20_000), s_feeRate, 200,
            "withdraw", new WalletSelectionPolicy(), TestContext.Current.CancellationToken);
        // Assert
        Assert.Equal(ordinary.TxId, Assert.Single(reservation.Inputs).TxId);
        Assert.False(_utxos.TryGetFeeReservation(silent.TxId, silent.Index, out _));
    }

    [Fact]
    public async Task Given_OrdinaryCoinsCannotCoverAmount_When_SingleSilentCoinCan_Then_ItIsSpentAlone()
    {
        // Arrange
        AddUtxo(10_000);
        var silent = AddSilentCoin(100_000, 7);
        // Act
        var reservation = await _selector.ReserveAsync(LightningMoney.Satoshis(20_000), s_feeRate, 200,
            "withdraw", new WalletSelectionPolicy(), TestContext.Current.CancellationToken);
        // Assert
        var input = Assert.Single(reservation.Inputs);
        Assert.Equal(silent.TxId, input.TxId);
        Assert.True(input.IsSilentPayment);
        Assert.Equal((uint)7, input.SilentPaymentLabel);
    }

    [Fact]
    public async Task Given_OnlySameLabelCanCoverAmount_When_Selecting_Then_OtherLabelsAreNotLinked()
    {
        // Arrange
        var first = AddSilentCoin(30_000, 7);
        var second = AddSilentCoin(30_000, 7);
        var other = AddSilentCoin(40_000, 8);
        // Act
        var reservation = await _selector.ReserveAsync(LightningMoney.Satoshis(50_000), s_feeRate, 200,
            "withdraw", new WalletSelectionPolicy(), TestContext.Current.CancellationToken);
        // Assert
        Assert.Equal(2, reservation.Inputs.Count);
        Assert.Contains(reservation.Inputs, i => i.TxId == first.TxId);
        Assert.Contains(reservation.Inputs, i => i.TxId == second.TxId);
        Assert.False(_utxos.TryGetFeeReservation(other.TxId, other.Index, out _));
    }

    [Fact]
    public async Task Given_AmountRequiresMixing_When_Selecting_Then_FallsBackRatherThanHidingCoins()
    {
        // Arrange
        AddUtxo(30_000);
        AddSilentCoin(30_000, 7);
        AddSilentCoin(30_000, 8);
        // Act
        var reservation = await _selector.ReserveAsync(LightningMoney.Satoshis(80_000), s_feeRate, 200,
            "withdraw", new WalletSelectionPolicy(), TestContext.Current.CancellationToken);
        // Assert
        Assert.Equal(3, reservation.Inputs.Count);
        Assert.Equal(2, reservation.Inputs.Count(i => i.IsSilentPayment));
    }

    [Fact]
    public async Task Given_TheSilentCoinChosen_When_OrdinaryCoinsCouldPay_Then_ExactlyTheChosenCoinIsReserved()
    {
        // Arrange (NL-1296: the explicit opt-in that AvoidMixing otherwise overrides)
        AddUtxo(300_000);
        var silent = AddSilentCoin(100_000, 7);
        var policy = new WalletSelectionPolicy { Inputs = [(silent.TxId, silent.Index)] };
        // Act
        var reservation = await _selector.ReserveAsync(LightningMoney.Satoshis(20_000), s_feeRate, 200,
            "withdraw", policy, TestContext.Current.CancellationToken);
        // Assert
        var input = Assert.Single(reservation.Inputs);
        Assert.Equal(silent.TxId, input.TxId);
        Assert.True(input.IsSilentPayment);
        Assert.True(reservation.ChangeAmount.Satoshi > 0);
        Assert.True(_utxos.TryGetFeeReservation(silent.TxId, silent.Index, out _));
    }

    [Fact]
    public async Task Given_AChosenCoinListedTwiceOrReserved_When_Selecting_Then_RefusedAndNothingReserved()
    {
        // Arrange
        var silent = AddSilentCoin(100_000, 7);
        var reserved = AddUtxo(50_000);
        Assert.True(_utxos.TryReserveForFee([(reserved.TxId, reserved.Index)], Guid.NewGuid()));
        // Act / Assert
        foreach (var inputs in new[]
                 {
                     new[] { (silent.TxId, silent.Index), (silent.TxId, silent.Index) },
                     new[] { (silent.TxId, silent.Index), (reserved.TxId, reserved.Index) }
                 })
        {
            var failure = await Assert.ThrowsAsync<WalletSpendException>(() => _selector.ReserveAsync(
                LightningMoney.Satoshis(20_000), s_feeRate, 200, "withdraw",
                new WalletSelectionPolicy { Inputs = inputs }, TestContext.Current.CancellationToken));
            Assert.Equal(WalletSpendError.InputUnavailable, failure.Error);
        }

        Assert.False(_utxos.TryGetFeeReservation(silent.TxId, silent.Index, out _));
    }

    private UtxoModel AddSilentCoin(long satoshis, uint label)
    {
        var output = new SilentPaymentOutputModel(new TxId(RandomUtils.GetUInt256().ToBytes()), 0,
            new Key().PubKey.ToBytes()[1..], new byte[32], label, satoshis, 100, new Hash(new byte[32]));
        var utxo = new UtxoModel(output);
        _utxos.Add(utxo);
        return utxo;
    }
}