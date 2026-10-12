using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.SilentPayments;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Domain.Exceptions;
using Domain.Money;

/// <summary>NL-1298: explicit input choice is frozen before BIP352 derivation and secure signing.</summary>
public partial class WalletSpendServiceTests
{
    [Fact]
    public async Task Given_ChosenOfficialInputsAndLargerUnchosenCoin_When_SilentWithdraw_Then_ExactVectorAndSignaturesMatch()
    {
        // Arrange: unrelated liquidity must never alter the BIP352 input hash or eligible-key sum.
        const string firstId = "f4184fc596403b9d638783cf57adfe4c75c605f6356fbc91338530e9831e9e16";
        const string secondId = "a1075db55d416d3ca199f55b6084e2115b9345e16c5cf302fc80e9d5fbf5d48d";
        var first = AddVectorInput(800, firstId, "eadc78165ff1f8ea94ad7cfdc54990738a4c53f6e0507b42154201b8e5dff3b1");
        var second = AddVectorInput(801, secondId, "93f5ed907ad5b2bdbbdcb5d9116ebc0a4e1f92f910d5260237fa45a9408aad16");
        var unrelated = AddWalletUtxo(AddressType.P2Wpkh, 3, 1_000_000);
        var chosen = new[] { (new TxId(uint256.Parse(secondId).ToBytes()), 0u),
                             (new TxId(uint256.Parse(firstId).ToBytes()), 0u) };
        // Act
        var result = await CreateSilentService().WithdrawAsync(new WalletWithdrawRequest(
            SilentPaymentAddressCodec.Encode(s_silentAddress), LightningMoney.Satoshis(300_000),
            LightningMoney.Satoshis(FeeRatePerKw))
        { Inputs = chosen }, TestContext.Current.CancellationToken);
        // Assert: preserve named order, independently known recipient output, P2TR change and both real signatures.
        var tx = AssertPublishedAndValid(second, first);
        Assert.Equal(chosen, tx.Inputs.Select(i => (new TxId(i.PrevOut.Hash.ToBytes()), i.PrevOut.N)));
        Assert.Equal("51203e9fce73d4e77a4809908e3c3a2e54ee147b9312dc5044a193d1fc85de46e3c1",
            Convert.ToHexStringLower(tx.Outputs[(int)result.DestinationOutputIndex].ScriptPubKey.ToBytes()));
        Assert.Equal(400_000 - 300_000 - result.Fee.Satoshi, result.Change.Satoshi);
        Assert.All(tx.Outputs, output => Assert.True(output.ScriptPubKey.IsScriptType(ScriptType.Taproot)));
        Assert.False(_utxos.TryGetFeeReservation(unrelated.Model.TxId, unrelated.Model.Index, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_ChosenSilentAndOrdinaryCoins_When_PayingSilentRecipient_Then_AmountAndAllSpendOnlyNamedInputs(bool all)
    {
        // Arrange: explicit choice opts into mixing; the other coins continue backing the anchors reserve.
        using var keys = new SilentCoinKeys(_keyManager.Object);
        var ordinary = AddWalletUtxo(AddressType.P2Wpkh, 0, 30_000);
        var silent = AddSilentCoin(keys, 40_000, 1);
        var unrelated = AddWalletUtxo(AddressType.P2Tr, 3, 800_000);
        _reserveSat = 70_000;
        var service = CreateSilentService(signer: CreateSigner(keys));
        var chosen = new[] { (silent.Model.TxId, silent.Model.Index), (ordinary.Model.TxId, ordinary.Model.Index) };
        // Act
        var result = await service.WithdrawAsync(new WalletWithdrawRequest(SilentPaymentAddressCodec.Encode(s_silentAddress),
            all ? null : LightningMoney.Satoshis(20_000), LightningMoney.Satoshis(FeeRatePerKw))
        { Inputs = chosen },
            TestContext.Current.CancellationToken);
        // Assert: raw SP input signs with its stored tweak (no BIP86 tweak); ordinary input also verifies.
        var tx = AssertPublishedAndValid(silent.TxOut, ordinary.TxOut);
        Assert.Equal(chosen, tx.Inputs.Select(i => (new TxId(i.PrevOut.Hash.ToBytes()), i.PrevOut.N)));
        Assert.Equal(all ? 70_000 - result.Fee.Satoshi : 20_000, result.Amount.Satoshi);
        Assert.Equal(all ? 0 : 70_000 - 20_000 - result.Fee.Satoshi, result.Change.Satoshi);
        Assert.Equal(all ? 1 : 2, tx.Outputs.Count);
        Assert.Equal(2, result.InputCount);
        Assert.False(_utxos.TryGetFeeReservation(unrelated.Model.TxId, unrelated.Model.Index, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_ChosenInputsCannotCoverAmountOrReserve_When_SilentWithdraw_Then_NoSubstitutionAndReservationReleased(bool reserve)
    {
        // Arrange
        var coin = AddWalletUtxo(AddressType.P2Wpkh, 0, 40_000);
        if (!reserve) AddWalletUtxo(AddressType.P2Wpkh, 1, 800_000);
        _reserveSat = reserve ? 70_000 : 0;
        var request = new WalletWithdrawRequest(SilentPaymentAddressCodec.Encode(s_silentAddress),
            reserve ? null : LightningMoney.Satoshis(50_000), LightningMoney.Satoshis(FeeRatePerKw))
        { Inputs = [(coin.Model.TxId, coin.Model.Index)] };
        // Act / Assert
        if (reserve)
            await Assert.ThrowsAsync<AnchorReserveException>(() => CreateSilentService().WithdrawAsync(request, TestContext.Current.CancellationToken));
        else
            await Assert.ThrowsAsync<InsufficientFundsException>(() => CreateSilentService().WithdrawAsync(request, TestContext.Current.CancellationToken));
        Assert.Empty(_stored);
        Assert.Empty(_published);
        Assert.False(_utxos.TryGetFeeReservation(coin.Model.TxId, coin.Model.Index, out _));
    }

    [Fact]
    public async Task Given_ExplicitSilentSendExceedsFeeLimit_When_Withdraw_Then_ChosenCoinReleasedWithoutPublication()
    {
        // Arrange
        var coin = AddWalletUtxo(AddressType.P2Wpkh, 0, 40_000);
        var request = new WalletWithdrawRequest(SilentPaymentAddressCodec.Encode(s_silentAddress),
            LightningMoney.Satoshis(20_000), LightningMoney.Satoshis(FeeRatePerKw))
        { Inputs = [(coin.Model.TxId, coin.Model.Index)], MaxFee = LightningMoney.Satoshis(1) };
        // Act / Assert
        var error = await Assert.ThrowsAsync<WalletSpendException>(() => CreateSilentService().WithdrawAsync(
            request, TestContext.Current.CancellationToken));
        Assert.Equal(WalletSpendError.FeeAboveLimit, error.Error);
        Assert.False(_utxos.TryGetFeeReservation(coin.Model.TxId, coin.Model.Index, out _));
        Assert.Empty(_stored);
        Assert.Empty(_published);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_DuplicateOrUnconfirmedChosenInput_When_SilentWithdrawAll_Then_RefusedBeforeReservation(bool duplicate)
    {
        // Arrange
        var coin = AddWalletUtxo(AddressType.P2Wpkh, 0, 40_000, blockHeight: duplicate ? Height : 0);
        var chosen = new List<(TxId TxId, uint Index)> { (coin.Model.TxId, coin.Model.Index) };
        if (duplicate) chosen.Add(chosen[0]);
        // Act / Assert
        var error = await Assert.ThrowsAsync<WalletSpendException>(() => CreateSilentService().WithdrawAsync(
            new WalletWithdrawRequest(SilentPaymentAddressCodec.Encode(s_silentAddress), null,
                LightningMoney.Satoshis(FeeRatePerKw))
            { Inputs = chosen }, TestContext.Current.CancellationToken));
        Assert.Equal(WalletSpendError.InputUnavailable, error.Error);
        Assert.Empty(_stored);
        Assert.Empty(_published);
    }

    [Fact]
    public async Task Given_SelectorSubstitutesChosenInput_When_SilentWithdraw_Then_RefusedBeforeDerivationAndReleased()
    {
        // Arrange: a selector adapter that ignores policy must not silently pay from another coin.
        var named = AddWalletUtxo(AddressType.P2Wpkh, 0, 40_000);
        var substitute = AddWalletUtxo(AddressType.P2Wpkh, 1, 80_000);
        var selector = new Mock<IFeeInputSelector>();
        var signer = new Mock<ILightningSigner>();
        var replacement = await _selector.ReserveAsync(LightningMoney.Satoshis(20_000),
            LightningMoney.Satoshis(FeeRatePerKw), 300, "withdraw", WalletSelectionPolicy.Default with
            { Inputs = [(substitute.Model.TxId, substitute.Model.Index)] }, TestContext.Current.CancellationToken);
        selector.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        selector.Setup(s => s.ReserveAsync(It.IsAny<LightningMoney>(), It.IsAny<LightningMoney>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<WalletSelectionPolicy>(), It.IsAny<CancellationToken>())).ReturnsAsync(replacement);
        selector.Setup(s => s.ReleaseAsync(replacement.Id, It.IsAny<CancellationToken>()))
            .Returns((Guid id, CancellationToken ct) => _selector.ReleaseAsync(id, ct));
        // Act / Assert
        var error = await Assert.ThrowsAsync<WalletSpendException>(() => CreateSilentService(signer: signer.Object,
            selector: selector.Object).WithdrawAsync(new WalletWithdrawRequest(SilentPaymentAddressCodec.Encode(s_silentAddress),
            LightningMoney.Satoshis(20_000), LightningMoney.Satoshis(FeeRatePerKw))
            { Inputs = [(named.Model.TxId, named.Model.Index)] }, TestContext.Current.CancellationToken));
        Assert.Equal(WalletSpendError.InputUnavailable, error.Error);
        signer.Verify(s => s.ComputeSilentPaymentOutputs(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<SilentPaymentAddress>>(),
            It.IsAny<IReadOnlyList<(TxId, uint)>>()), Times.Never);
        Assert.False(_utxos.TryGetFeeReservation(substitute.Model.TxId, substitute.Model.Index, out _));
        Assert.Empty(_stored);
        Assert.Empty(_published);
    }
}