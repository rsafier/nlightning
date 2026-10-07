using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.SilentPayments;
using Domain.Bitcoin.SilentPayments.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Protocol.Interfaces;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Crypto.SilentPayments;
using Infrastructure.Bitcoin.Managers;
using Infrastructure.Bitcoin.Signers;
using Infrastructure.Bitcoin.Wallet.SilentPayments;

public partial class WalletSpendServiceTests
{
    [Fact]
    public async Task Given_SilentChangeEnabled_When_SendThenScanRestoreAndSpend_Then_LabelZeroCoinIsRecoverableAndSpendable()
    {
        // Arrange
        using var receiver = new ChangeKeys();
        var coin = AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        var options = new SilentPaymentsOptions { Enabled = true, Receive = true, ChangeToSilentPayment = true };
        var sender = CreateSilentService(options, keys: receiver.Keys);
        // Act: derive a private m=0 change output, then independently scan the confirmed transaction.
        var sent = await sender.WithdrawAsync(new WalletWithdrawRequest(SilentPaymentAddressCodec.Encode(s_silentAddress),
            LightningMoney.Satoshis(20_000), LightningMoney.Satoshis(FeeRatePerKw)), TestContext.Current.CancellationToken);
        var tx = AssertPublishedAndValid(coin.TxOut);
        var prevouts = new ChangePrevouts(tx, coin.TxOut);
        var block = Network.RegTest.Consensus.ConsensusFactory.CreateBlock();
        block.Transactions.Add(tx);
        block.UpdateMerkleRoot();
        var serialized = new BitcoinBlock(block.ToBytes(), new Hash(block.GetHash().ToBytes()), 201);
        var scanner = CreateChangeScanner(prevouts, receiver.Keys, options);
        IReadOnlyList<SilentPaymentOutputModel> scanned;
        using (await scanner.EnterAsync(TestContext.Current.CancellationToken))
            scanned = await scanner.PrepareAsync(serialized, 201, [], TestContext.Current.CancellationToken);
        // Assert: no deposit address was allocated for the SP change and a fresh scanner restores the same coin.
        var change = Assert.Single(scanned);
        Assert.Equal((uint?)0, change.Label);
        Assert.Equal(sent.Change.Satoshi, change.AmountSats);
        Assert.False(change.Ignored);
        Assert.Null(Assert.Single(_stored).ChangeScript);
        _walletService.Verify(w => w.GetUnusedAddressAsync(It.IsAny<AddressType>(), true), Times.Never);
        var restoredScanner = CreateChangeScanner(prevouts, receiver.Keys, options);
        IReadOnlyList<SilentPaymentOutputModel> restored;
        using (await restoredScanner.EnterAsync(TestContext.Current.CancellationToken))
            restored = await restoredScanner.PrepareAsync(serialized, 201, [], TestContext.Current.CancellationToken);
        Assert.Equal(change.OutputKey, Assert.Single(restored).OutputKey);
        Assert.Equal(change.Tweak, Assert.Single(restored).Tweak);
        // Spend the restored output through the real wallet signer and verify its key-path signature.
        _utxos.Spend(coin.Model);
        var received = new UtxoModel(change);
        Assert.Null(received.WalletAddress);
        _utxos.Add(received);
        var signer = new LocalLightningSigner(Mock.Of<IFundingOutputBuilder>(), Mock.Of<IKeyDerivationService>(),
            NullLogger<LocalLightningSigner>.Instance, _nodeOptions, receiver.Keys, _utxos);
        await CreateSilentService(signer: signer).WithdrawAsync(Request(10_000), TestContext.Current.CancellationToken);
        var spent = Transaction.Load(Assert.Single(_published.Skip(1)).RawTransaction, Network.RegTest);
        var validator = spent.CreateValidator([tx.Outputs[(int)change.Index]]);
        Assert.True(validator.ValidateInput(0).Error is null or ScriptError.OK);
    }

    [Fact]
    public async Task Given_ChangeBelowReceivingMinimum_When_SilentChangeRequested_Then_FallsBackToSpendableBip86Change()
    {
        // Arrange
        using var receiver = new ChangeKeys();
        var coin = AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        var options = new SilentPaymentsOptions { Enabled = true, ChangeToSilentPayment = true, MinReceiveSat = 80_000 };
        // Act
        var sent = await CreateSilentService(options, keys: receiver.Keys).WithdrawAsync(
            new WalletWithdrawRequest(SilentPaymentAddressCodec.Encode(s_silentAddress), LightningMoney.Satoshis(20_000),
                LightningMoney.Satoshis(FeeRatePerKw)), TestContext.Current.CancellationToken);
        // Assert
        var tx = AssertPublishedAndValid(coin.TxOut);
        Assert.True(sent.Change.Satoshi < options.MinReceiveSat);
        var script = Assert.Single(_stored).ChangeScript;
        Assert.NotNull(script);
        Assert.Equal((byte[])script.Value, Assert.Single(tx.Outputs, o => o.Value.Satoshi == sent.Change.Satoshi).ScriptPubKey.ToBytes());
        Assert.Equal(34, ((byte[])script.Value).Length);
    }

    [Fact]
    public async Task Given_ReceivingDisabled_When_SilentChangeRequested_Then_RefusedBeforeReservation()
    {
        // Arrange
        using var receiver = new ChangeKeys();
        AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        var options = new SilentPaymentsOptions { Enabled = true, ChangeToSilentPayment = true, Receive = false };
        // Act
        var error = await Assert.ThrowsAsync<WalletSpendException>(() => CreateSilentService(options, keys: receiver.Keys)
            .WithdrawAsync(new WalletWithdrawRequest(SilentPaymentAddressCodec.Encode(s_silentAddress),
                LightningMoney.Satoshis(20_000), null), TestContext.Current.CancellationToken));
        // Assert
        Assert.Contains("Receive=true", error.Message);
        Assert.Empty(_stored);
        Assert.Empty(_published);
    }

    private static SilentPaymentScanner CreateChangeScanner(IBlockPrevoutSource prevouts, ISilentPaymentKeySource keys,
                                                            SilentPaymentsOptions options) =>
        new(prevouts, new SilentPaymentCrypto(), keys, Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<SilentPaymentScanner>.Instance);

    private sealed class ChangePrevouts(Transaction transaction, params TxOut[] inputs) : IBlockPrevoutSource
    {
        public SilentPaymentPrevoutSource Source => SilentPaymentPrevoutSource.GetRawTransaction;
        public Task ProbeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ValidateHeightAsync(uint height, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyDictionary<TxId, IReadOnlyList<BitcoinPrevout>>> GetPrevoutsAsync(BitcoinBlock block,
            uint height, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<TxId, IReadOnlyList<BitcoinPrevout>>>(
                new Dictionary<TxId, IReadOnlyList<BitcoinPrevout>>
                {
                    [new TxId(transaction.GetHash().ToBytes())] = inputs.Select(input =>
                        new BitcoinPrevout((ulong)input.Value.Satoshi, input.ScriptPubKey.ToBytes())).ToArray()
                });
    }

    private sealed class ChangeKeys : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "nltg-sp-change-" + Guid.NewGuid().ToString("N"));
        public SecureKeyManager Keys { get; }
        public ChangeKeys()
        {
            Directory.CreateDirectory(_directory);
            Keys = SecureKeyManager.FromMnemonic("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about",
                string.Empty, BitcoinNetwork.Regtest, Path.Combine(_directory, "keys.json"));
        }
        public void Dispose()
        {
            Keys.Dispose();
            Directory.Delete(_directory, true);
        }
    }
}