using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.SilentPayments;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Infrastructure.Bitcoin.Wallet;

public partial class WalletSpendServiceTests
{
    private static readonly SilentPaymentAddress s_silentAddress = new(0,
        new CompactPubKey(Convert.FromHexString("0220bcfac5b99e04ad1a06ddfb016ee13582609d60b6291e98d01a9bc9a16c96d4")),
        new CompactPubKey(Convert.FromHexString("025cc9856d6f8375350e123978daac200c260cb5b5ae83106cab90484dcd8fcf36")), "sprt");

    private WalletSpendService CreateSilentService(SilentPaymentsOptions? options = null, ILightningSigner? signer = null)
    {
        var change = GetP2TrExtKey(50, true).Neuter().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Network.RegTest);
        _walletService.Setup(w => w.GetUnusedAddressAsync(AddressType.P2Tr, true))
                      .ReturnsAsync(new WalletAddressModel(AddressType.P2Tr, 50, true, change.ToString()));
        return new WalletSpendService(_selector, _anchorReserve.Object, _utxos, signer ?? _signer, _monitor.Object,
            _feeService.Object, _scopeFactory, Microsoft.Extensions.Options.Options.Create(_nodeOptions), NullLogger<WalletSpendService>.Instance,
            silentPayments: Microsoft.Extensions.Options.Options.Create(options ?? new SilentPaymentsOptions { Enabled = true }));
    }

    [Fact]
    public async Task Given_OfficialSenderInputs_When_WithdrawToSilentAddress_Then_VectorOutputAndRealSignaturesMatch()
    {
        // Arrange: the first BIP 352 vector's outpoints and private keys, as eligible P2WPKH wallet coins.
        var first = AddVectorInput(800,
            "f4184fc596403b9d638783cf57adfe4c75c605f6356fbc91338530e9831e9e16",
            "eadc78165ff1f8ea94ad7cfdc54990738a4c53f6e0507b42154201b8e5dff3b1");
        var second = AddVectorInput(801,
            "a1075db55d416d3ca199f55b6084e2115b9345e16c5cf302fc80e9d5fbf5d48d",
            "93f5ed907ad5b2bdbbdcb5d9116ebc0a4e1f92f910d5260237fa45a9408aad16");
        var service = CreateSilentService();
        // Act
        var result = await service.WithdrawAsync(new WalletWithdrawRequest(SilentPaymentAddressCodec.Encode(s_silentAddress),
            LightningMoney.Satoshis(300_000), LightningMoney.Satoshis(FeeRatePerKw)), TestContext.Current.CancellationToken);
        // Assert
        var tx = AssertPublishedAndValid(first, second);
        Assert.Equal("51203e9fce73d4e77a4809908e3c3a2e54ee147b9312dc5044a193d1fc85de46e3c1",
                     Convert.ToHexStringLower(tx.Outputs[0].ScriptPubKey.ToBytes()));
        Assert.Equal(300_000, result.Amount.Satoshi);
        Assert.Equal(34, tx.Outputs[1].ScriptPubKey.Length);
    }

    [Fact]
    public async Task Given_MixedAndRepeatedRecipients_When_Send_Then_OutputsAreDistinctAndInputsVerify()
    {
        // Arrange
        var coin = AddWalletUtxo(AddressType.P2Wpkh, 0, 200_000);
        var service = CreateSilentService();
        var address = SilentPaymentAddressCodec.Encode(s_silentAddress);
        // Act
        await service.SendAsync([new WalletRecipient(address, LightningMoney.Satoshis(20_000)),
            new WalletRecipient(s_destination.ToString(), LightningMoney.Satoshis(30_000)),
            new WalletRecipient(address, LightningMoney.Satoshis(40_000))],
            LightningMoney.Satoshis(FeeRatePerKw), cancellationToken: TestContext.Current.CancellationToken);
        // Assert
        var tx = AssertPublishedAndValid(coin.TxOut);
        Assert.Equal(s_destination.ScriptPubKey, tx.Outputs[1].ScriptPubKey);
        Assert.NotEqual(tx.Outputs[0].ScriptPubKey, tx.Outputs[2].ScriptPubKey);
        Assert.Equal(new long[] { 20_000, 30_000, 40_000 }, tx.Outputs.Take(3).Select(o => o.Value.Satoshi));
    }

    [Fact]
    public async Task Given_SendAll_When_SilentPayment_Then_SpendsAllWithoutChange()
    {
        // Arrange
        var first = AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        var second = AddWalletUtxo(AddressType.P2Tr, 1, 50_000);
        var service = CreateSilentService();
        // Act
        var result = await service.WithdrawAsync(new WalletWithdrawRequest(SilentPaymentAddressCodec.Encode(s_silentAddress),
            null, LightningMoney.Satoshis(FeeRatePerKw)), TestContext.Current.CancellationToken);
        // Assert
        var tx = AssertPublishedAndValid(first.TxOut, second.TxOut);
        Assert.Single(tx.Outputs);
        Assert.Equal(150_000 - result.Fee.Satoshi, result.Amount.Satoshi);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Given_DisabledSilentPayments_When_Withdraw_Then_NoCoinIsReserved(bool enabled, bool send)
    {
        // Arrange
        AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        var service = CreateSilentService(new SilentPaymentsOptions { Enabled = enabled, Send = send });
        // Act / Assert
        await Assert.ThrowsAsync<WalletSpendException>(() => service.WithdrawAsync(new WalletWithdrawRequest(
            SilentPaymentAddressCodec.Encode(s_silentAddress), LightningMoney.Satoshis(20_000), null),
            TestContext.Current.CancellationToken));
        Assert.Empty(_stored);
        Assert.Empty(_published);
    }

    [Theory]
    [InlineData(545, "sprt")]
    [InlineData(20_000, "sp")]
    public async Task Given_DustOrWrongNetwork_When_SilentWithdraw_Then_NoCoinIsReserved(long amount, string hrp)
    {
        // Arrange
        AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        var service = CreateSilentService();
        // Act / Assert
        await Assert.ThrowsAsync<WalletSpendException>(() => service.WithdrawAsync(new WalletWithdrawRequest(
            SilentPaymentAddressCodec.Encode(s_silentAddress with { Hrp = hrp }), LightningMoney.Satoshis(amount), null),
            TestContext.Current.CancellationToken));
        Assert.Empty(_stored);
        Assert.Empty(_published);
    }

    [Fact]
    public async Task Given_InputsChangedAfterDerivation_When_Signing_Then_RefusedAndReservationReleased()
    {
        // Arrange
        AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        var signer = new Mock<ILightningSigner>();
        signer.Setup(s => s.ComputeSilentPaymentOutputs(It.IsAny<Guid>(),
            It.IsAny<IReadOnlyList<SilentPaymentAddress>>(), It.IsAny<IReadOnlyList<(TxId, uint)>>()))
            .Callback(() => ((List<WalletInput>)Assert.Single(_stored).Inputs).Clear())
            .Returns(new BitcoinScript[] { new byte[] { 0x51, 0x20 }.Concat(((byte[])s_silentAddress.SpendKey)[1..]).ToArray() });
        var service = CreateSilentService(signer: signer.Object);
        // Act / Assert
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => service.WithdrawAsync(
            new WalletWithdrawRequest(SilentPaymentAddressCodec.Encode(s_silentAddress), LightningMoney.Satoshis(20_000), null),
            TestContext.Current.CancellationToken));
        Assert.Contains("derive again", failure.Message);
        Assert.Empty(_stored);
        Assert.Empty(_published);
    }

    [Fact]
    public async Task Given_MainnetGateClosed_When_SilentWithdraw_Then_RefusedBeforeReservation()
    {
        // Arrange
        _nodeOptions.BitcoinNetwork = "mainnet";
        var service = CreateSilentService();
        // Act / Assert
        var failure = await Assert.ThrowsAsync<WalletSpendException>(() => service.WithdrawAsync(
            new WalletWithdrawRequest(SilentPaymentAddressCodec.Encode(s_silentAddress with { Hrp = "sp" }),
                LightningMoney.Satoshis(20_000), null), TestContext.Current.CancellationToken));
        Assert.Contains("AllowMainnet", failure.Message);
        Assert.Empty(_stored);
    }

    private TxOut AddVectorInput(uint index, string txid, string privateKey)
    {
        var key = new Key(Convert.FromHexString(privateKey));
        var address = key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        _keyManager.Setup(k => k.GetDepositP2WpkhKeyAtIndex(index, false))
                   .Returns(new ExtKey(key, new byte[32]).ToBytes());
        var outpoint = new OutPoint(uint256.Parse(txid), 0);
        var model = new UtxoModel(new TxId(outpoint.Hash.ToBytes()), 0, LightningMoney.Satoshis(200_000), Height,
            new WalletAddressModel(AddressType.P2Wpkh, index, false, address.ToString()));
        _utxos.Add(model);
        return new TxOut(Money.Satoshis(200_000), address.ScriptPubKey);
    }
}