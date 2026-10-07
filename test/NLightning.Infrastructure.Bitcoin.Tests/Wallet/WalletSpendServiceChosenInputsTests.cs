using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.SilentPayments;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Protocol.Interfaces;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Managers;
using Infrastructure.Bitcoin.Signers;

/// <summary>
/// NL-1296: a received silent payment coin is an ordinary wallet output (balance, reserve), but
/// <c>SilentPayments:AvoidMixing</c> keeps it out of a withdraw the other coins can pay, so a node with an anchors
/// reserve larger than the coin could never spend it. <c>withdraw --utxo</c> (<see cref="WalletWithdrawRequest.Inputs"/>)
/// spends exactly the chosen outputs, the silent payment coin included.
/// </summary>
public partial class WalletSpendServiceTests
{
    [Fact]
    public async Task Given_AnAnchorsReserveAboveTheSilentCoin_When_WithdrawAll_Then_TheSilentCoinIsKeptOutOfTheSpend()
    {
        // Arrange: FAFO2's shape, ordinary coins that pay the withdraw and a 40,000 sat silent payment coin under a
        // 70,000 sat reserve (D-SP13: the coin is not linked to the others when they suffice)
        using var keys = new SilentCoinKeys(_keyManager.Object);
        var ordinary = AddWalletUtxo(AddressType.P2Wpkh, 0, 800_000);
        var silent = AddSilentCoin(keys, 40_000, 1);
        _reserveSat = 70_000;
        var service = CreateSilentService(signer: CreateSigner(keys));

        // Act
        await service.WithdrawAsync(new WalletWithdrawRequest(s_destination.ToString(), null,
                                                              LightningMoney.Satoshis(FeeRatePerKw)),
                                    TestContext.Current.CancellationToken);

        // Assert
        var tx = AssertPublishedAndValid(ordinary.TxOut);
        Assert.DoesNotContain(tx.Inputs, i => i.PrevOut == silent.OutPoint);
        Assert.False(_utxos.TryGetFeeReservation(silent.Model.TxId, silent.Model.Index, out _));
    }

    [Fact]
    public async Task Given_TheSilentCoinChosen_When_WithdrawAll_Then_ExactlyItIsSpentWithoutChange()
    {
        // Arrange
        using var keys = new SilentCoinKeys(_keyManager.Object);
        AddWalletUtxo(AddressType.P2Wpkh, 0, 800_000);
        var silent = AddSilentCoin(keys, 40_000, 1);
        _reserveSat = 70_000;
        var service = CreateSilentService(signer: CreateSigner(keys));

        // Act
        var result = await service.WithdrawAsync(
            new WalletWithdrawRequest(s_destination.ToString(), null, LightningMoney.Satoshis(FeeRatePerKw))
            {
                Inputs = [(silent.Model.TxId, silent.Model.Index)]
            }, TestContext.Current.CancellationToken);

        // Assert: one input, the silent coin, signed by key path; everything but the fee goes to the destination
        var tx = AssertPublishedAndValid(silent.TxOut);
        Assert.Equal(silent.OutPoint, Assert.Single(tx.Inputs).PrevOut);
        var output = Assert.Single(tx.Outputs);
        Assert.Equal(s_destination.ScriptPubKey, output.ScriptPubKey);
        Assert.Equal(40_000 - result.Fee.Satoshi, output.Value.Satoshi);
        Assert.Equal(0, result.Change.Satoshi);
        Assert.Equal(1, result.InputCount);
    }

    [Fact]
    public async Task Given_TheSilentCoinChosen_When_WithdrawingAnAmount_Then_TheRestComesBackAsChange()
    {
        // Arrange
        using var keys = new SilentCoinKeys(_keyManager.Object);
        AddWalletUtxo(AddressType.P2Wpkh, 0, 800_000);
        var silent = AddSilentCoin(keys, 40_000, null);
        var service = CreateSilentService(signer: CreateSigner(keys));

        // Act
        var result = await service.WithdrawAsync(
            new WalletWithdrawRequest(s_destination.ToString(), LightningMoney.Satoshis(10_000),
                                      LightningMoney.Satoshis(FeeRatePerKw))
            {
                Inputs = [(silent.Model.TxId, silent.Model.Index)]
            }, TestContext.Current.CancellationToken);

        // Assert
        var tx = AssertPublishedAndValid(silent.TxOut);
        Assert.Equal(2, tx.Outputs.Count);
        Assert.Contains(tx.Outputs, o => o.ScriptPubKey == s_destination.ScriptPubKey && o.Value.Satoshi == 10_000);
        Assert.Contains(tx.Outputs, o => o.ScriptPubKey == _changeAddress.ScriptPubKey
                                      && o.Value.Satoshi == result.Change.Satoshi);
        Assert.Equal(40_000 - 10_000 - result.Fee.Satoshi, result.Change.Satoshi);
    }

    [Fact]
    public async Task Given_AnOutputTheWalletCannotSpend_When_Chosen_Then_RefusedAndNothingReserved()
    {
        // Arrange
        AddWalletUtxo(AddressType.P2Wpkh, 0, 800_000);
        var unconfirmed = AddWalletUtxo(AddressType.P2Wpkh, 1, 50_000, blockHeight: 0);
        var unknown = (new TxId(RandomUtils.GetBytes(32)), 3u);

        // Act / Assert
        foreach (var chosen in new[] { (unconfirmed.Model.TxId, unconfirmed.Model.Index), unknown })
        {
            var failure = await Assert.ThrowsAsync<WalletSpendException>(() => _service.WithdrawAsync(
                new WalletWithdrawRequest(s_destination.ToString(), null, LightningMoney.Satoshis(FeeRatePerKw))
                {
                    Inputs = [chosen]
                }, TestContext.Current.CancellationToken));
            Assert.Equal(WalletSpendError.InputUnavailable, failure.Error);
        }

        Assert.Empty(_stored);
        Assert.Empty(_published);
    }

    [Fact]
    public async Task Given_TheChosenCoinsCannotPayTheAmount_When_Withdrawing_Then_InsufficientFundsAndNothingReserved()
    {
        // Arrange: the wallet could pay it, but only with coins the operator did not choose
        AddWalletUtxo(AddressType.P2Wpkh, 0, 800_000);
        var chosen = AddWalletUtxo(AddressType.P2Wpkh, 1, 20_000);

        // Act / Assert
        await Assert.ThrowsAsync<InsufficientFundsException>(() => _service.WithdrawAsync(
            new WalletWithdrawRequest(s_destination.ToString(), LightningMoney.Satoshis(30_000),
                                      LightningMoney.Satoshis(FeeRatePerKw))
            {
                Inputs = [(chosen.Model.TxId, chosen.Model.Index)]
            }, TestContext.Current.CancellationToken));
        Assert.Empty(_stored);
        Assert.Empty(_published);
    }

    [Fact]
    public async Task Given_ChosenInputs_When_PayingASilentPaymentAddress_Then_Refused()
    {
        // Arrange
        var coin = AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        var service = CreateSilentService();

        // Act / Assert
        var failure = await Assert.ThrowsAsync<WalletSpendException>(() => service.WithdrawAsync(
            new WalletWithdrawRequest(SilentPaymentAddressCodec.Encode(s_silentAddress), LightningMoney.Satoshis(20_000),
                                      null)
            {
                Inputs = [(coin.Model.TxId, coin.Model.Index)]
            }, TestContext.Current.CancellationToken));
        Assert.Equal(WalletSpendError.InputUnavailable, failure.Error);
        Assert.Empty(_stored);
    }

    private LocalLightningSigner CreateSigner(ISecureKeyManager keys) =>
        new(Mock.Of<IFundingOutputBuilder>(), Mock.Of<IKeyDerivationService>(), NullLogger<LocalLightningSigner>.Instance,
            _nodeOptions, keys, _utxos);

    private (UtxoModel Model, OutPoint OutPoint, TxOut TxOut) AddSilentCoin(SilentCoinKeys keys, long amountSat,
                                                                            uint? label)
    {
        var tweak = RandomNumberGenerator.GetBytes(32);
        tweak[0] = 0; // below the curve order
        var secret = keys.GetSilentPaymentSpendKey(tweak, label);
        try
        {
            using var key = new Key(secret);
            var outPoint = new OutPoint(RandomUtils.GetUInt256(), 0);
            var outputKey = key.PubKey.ToBytes().AsSpan(1).ToArray();
            var model = new UtxoModel(new SilentPaymentOutputModel(new TxId(outPoint.Hash.ToBytes()), 0, outputKey,
                                                                   tweak, label, amountSat, 100,
                                                                   new Hash(new byte[32])));
            _utxos.Add(model);
            return (model, outPoint, new TxOut(Money.Satoshis(amountSat), new Script([0x51, 0x20, .. outputKey])));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>
    /// The test's deposit keys (the mocked key manager) with a real silent payment spend key, which a mock cannot give:
    /// <see cref="ISecureKeyManager.GetSilentPaymentSpendKey"/> takes a span.
    /// </summary>
    private sealed class SilentCoinKeys : ISecureKeyManager, IDisposable
    {
        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), "nltg-sp-withdraw-" + Guid.NewGuid().ToString("N"));
        private readonly ISecureKeyManager _deposits;
        private readonly SecureKeyManager _silent;

        public SilentCoinKeys(ISecureKeyManager deposits)
        {
            _deposits = deposits;
            Directory.CreateDirectory(_directory);
            _silent = SecureKeyManager.FromMnemonic(
                "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about",
                string.Empty, BitcoinNetwork.Regtest, Path.Combine(_directory, "keys.json"));
        }

        public byte[] GetSilentPaymentSpendKey(ReadOnlySpan<byte> tweak32, uint? label) =>
            _silent.GetSilentPaymentSpendKey(tweak32, label);

        public BitcoinKeyPath ChannelKeyPath => _silent.ChannelKeyPath;
        public uint HeightOfBirth => _silent.HeightOfBirth;
        public ExtPrivKey GetNextChannelKey(out uint index) => _silent.GetNextChannelKey(out index);
        public ExtPrivKey GetChannelKeyAtIndex(uint index) => _silent.GetChannelKeyAtIndex(index);

        public ExtPrivKey GetDepositP2TrKeyAtIndex(uint index, bool isChange) =>
            _deposits.GetDepositP2TrKeyAtIndex(index, isChange);

        public ExtPrivKey GetDepositP2WpkhKeyAtIndex(uint index, bool isChange) =>
            _deposits.GetDepositP2WpkhKeyAtIndex(index, isChange);

        public CryptoKeyPair GetNodeKeyPair() => _silent.GetNodeKeyPair();
        public CompactPubKey GetNodePubKey() => _silent.GetNodePubKey();

        public void ComputeNodeSharedSecret(ReadOnlySpan<byte> publicKey, Span<byte> sharedSecret) =>
            _silent.ComputeNodeSharedSecret(publicKey, sharedSecret);

        public void Dispose()
        {
            _silent.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}