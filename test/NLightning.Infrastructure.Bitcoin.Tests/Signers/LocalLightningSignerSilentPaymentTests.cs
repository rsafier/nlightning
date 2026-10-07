using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Signers;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.SilentPayments;
using Domain.Bitcoin.SilentPayments.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.ValueObjects;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Crypto.SilentPayments;
using Infrastructure.Bitcoin.Managers;
using Infrastructure.Bitcoin.Outputs;
using Infrastructure.Bitcoin.Signers;
using Wallet;

public sealed class LocalLightningSignerSilentPaymentTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nltg-sp-signer-" + Guid.NewGuid().ToString("N"));
    private readonly SecureKeyManager _keys;
    private readonly TrackingSignerKeys _signingKeys;
    private readonly FakeWalletUtxoRepository _utxos = new();
    private readonly LocalLightningSigner _signer;

    public LocalLightningSignerSilentPaymentTests()
    {
        Directory.CreateDirectory(_directory);
        _keys = SecureKeyManager.FromMnemonic(
            "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about",
            string.Empty, BitcoinNetwork.Regtest, Path.Combine(_directory, "keys.json"));
        _signingKeys = new TrackingSignerKeys(_keys);
        _signer = new LocalLightningSigner(new FundingOutputBuilder(), Mock.Of<IKeyDerivationService>(),
            NullLogger<LocalLightningSigner>.Instance, new NodeOptions { BitcoinNetwork = "regtest" }, _signingKeys, _utxos);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(uint.MaxValue)]
    public void Given_AReservedSilentPaymentCoin_When_Spending_Then_TheRawKeyPathSignatureVerifies(uint? label)
    {
        // Arrange: golden key fixtures exercise both odd and even output-key parity.
        var reservation = Guid.NewGuid();
        var silent = AddSilent(label, reservation);
        var deposit = AddDeposit(AddressType.P2Tr, 2, reservation);
        var tx = CreateSpend(silent.Outpoint, deposit.Outpoint);
        var signed = ToSigned(tx);

        // Act
        Assert.True(_signer.SignWalletTransaction(signed, reservation, []));

        // Assert: an accidental BIP86 tweak fails the independent script interpreter.
        var result = Transaction.Load(signed.RawTxBytes, Network.RegTest);
        var validator = result.CreateValidator([silent.PrevOut, deposit.PrevOut]);
        Assert.All(Enumerable.Range(0, 2), index => Assert.True(validator.ValidateInput(index).Error is null or ScriptError.OK));
        Assert.All(result.Inputs, input => Assert.Single(input.WitScript.Pushes));
        AssertWipedSignerScalars();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    public void Given_ASilentPaymentCoinLockedForFunding_When_Signing_Then_FundingAndWalletInputsVerify(uint? label)
    {
        // Arrange: channel opens use another signing entry point than withdrawals and fee spends.
        var channel = new ChannelId(RandomUtils.GetBytes(32));
        var silent = AddSilent(label);
        var deposit = AddDeposit(AddressType.P2Wpkh, 3);
        silent.Model.LockedToChannelId = channel;
        deposit.Model.LockedToChannelId = channel;
        using var local = new Key(Enumerable.Repeat((byte)7, 32).ToArray());
        using var remote = new Key(Enumerable.Repeat((byte)8, 32).ToArray());
        var output = new FundingOutput(LightningMoney.Satoshis(50_000), local.PubKey, remote.PubKey).ToTxOut();
        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new TxIn(silent.Outpoint));
        tx.Inputs.Add(new TxIn(deposit.Outpoint));
        tx.Outputs.Add(output);
        _signer.RegisterChannel(channel, new ChannelSigningInfo(tx.GetHash().ToBytes(), 0, 50_000,
            local.PubKey.ToBytes(), remote.PubKey.ToBytes(), 0));
        var signed = ToSigned(tx);

        // Act
        Assert.True(_signer.SignFundingTransaction(channel, signed));

        // Assert
        var result = Transaction.Load(signed.RawTxBytes, Network.RegTest);
        var validator = result.CreateValidator([silent.PrevOut, deposit.PrevOut]);
        Assert.True(validator.ValidateInput(0).Error is null or ScriptError.OK);
        Assert.True(validator.ValidateInput(1).Error is null or ScriptError.OK);
    }

    [Fact]
    public void Given_AForgedRecordedSilentPaymentKey_When_Signing_Then_ItIsRefusedWithoutChangingTheTransaction()
    {
        // Arrange
        var reservation = Guid.NewGuid();
        var silent = AddSilent(0, reservation);
        silent.Model.SilentPayment!.OutputKey[0] ^= 1;
        var signed = ToSigned(CreateSpend(silent.Outpoint));
        var before = signed.RawTxBytes.ToArray();

        // Act / Assert
        Assert.Throws<SignerException>(() => _signer.SignWalletTransaction(signed, reservation, []));
        Assert.Equal(before, signed.RawTxBytes);
        AssertWipedSignerScalars();
    }

    [Theory]
    [InlineData(AddressType.P2Wpkh, false)]
    [InlineData(AddressType.P2Tr, false)]
    [InlineData(AddressType.P2Tr, true)]
    public void Given_FrozenWalletInputs_When_DerivingASilentPayment_Then_TheReceiverFindsBothOutputs(
        AddressType type, bool useSilentCoin)
    {
        // Arrange: repeat recipient scan keys advance k, and BIP86 inputs require the actual tweaked output scalar.
        var reservation = Guid.NewGuid();
        var input = useSilentCoin ? AddSilent(uint.MaxValue, reservation) : AddDeposit(type, 2, reservation);
        var recipient = new SilentPaymentAddress(0, _keys.ScanPubKey, _keys.SpendPubKey, "sprt");
        (TxId TxId, uint Index)[] frozen = [(input.Model.TxId, input.Model.Index)];

        // Act
        var scripts = _signer.ComputeSilentPaymentOutputs(reservation, [recipient, recipient], frozen);

        // Assert: use only public spent-output keys plus the receiver's private scan operation.
        var publicKey = type == AddressType.P2Tr
            ? new CompactPubKey([2, .. input.PrevOut.ScriptPubKey.ToBytes().AsSpan(2)])
            : DepositPubKey(2);
        var outpoint = new byte[36];
        ((byte[])input.Model.TxId).CopyTo(outpoint, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(outpoint.AsSpan(32), input.Model.Index);
        var hash = Bip352.ComputeInputHash(outpoint, publicKey);
        var tweakedPublic = Bip352.TweakInputPublicKey(publicKey, hash);
        var shared = new byte[33];
        try
        {
            _keys.ComputeScanSharedSecret(tweakedPublic, shared);
            var candidates = scripts.Select((script, index) =>
                new SilentPaymentScanCandidate((uint)index, ((byte[])script).AsSpan(2).ToArray())).ToArray();
            var matches = Bip352.Scan(shared, _keys.SpendPubKey, candidates);
            Assert.Equal(2, matches.Count);
            Assert.NotEqual((byte[])scripts[0], (byte[])scripts[1]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
        }
    }

    [Fact]
    public void Given_AReservationWithMoreInputs_When_DerivingFromASubset_Then_NoScriptsAreProduced()
    {
        // Arrange
        var reservation = Guid.NewGuid();
        var first = AddDeposit(AddressType.P2Wpkh, 1, reservation);
        AddDeposit(AddressType.P2Wpkh, 2, reservation);
        var recipient = new SilentPaymentAddress(0, _keys.ScanPubKey, _keys.SpendPubKey, "sprt");

        // Act / Assert
        Assert.Throws<SignerException>(() => _signer.ComputeSilentPaymentOutputs(reservation, [recipient],
            [(first.Model.TxId, first.Model.Index)]));
    }

    private (UtxoModel Model, OutPoint Outpoint, TxOut PrevOut) AddSilent(uint? label, Guid? reservation = null)
    {
        var tweak = Convert.FromHexString(new string('0', 63) + "1");
        var secret = _keys.GetSilentPaymentSpendKey(tweak, label);
        try
        {
            using var key = new Key(secret);
            var outpoint = new OutPoint(RandomUtils.GetUInt256(), 0);
            var outputKey = key.PubKey.ToBytes().AsSpan(1).ToArray();
            var model = new UtxoModel(new SilentPaymentOutputModel(new TxId(outpoint.Hash.ToBytes()), 0, outputKey,
                tweak, label, 75_000, 100, new Domain.Crypto.ValueObjects.Hash(new byte[32])));
            _utxos.Add(model);
            if (reservation is { } id)
                Assert.True(_utxos.TryReserveForFee([(model.TxId, model.Index)], id));
            return (model, outpoint, new TxOut(Money.Satoshis(75_000), new Script([0x51, 0x20, .. outputKey])));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private (UtxoModel Model, OutPoint Outpoint, TxOut PrevOut) AddDeposit(AddressType type, uint index, Guid? reservation = null)
    {
        byte[] extended = type == AddressType.P2Tr
            ? _keys.GetDepositP2TrKeyAtIndex(index, false)
            : _keys.GetDepositP2WpkhKeyAtIndex(index, false);
        try
        {
            using var key = ExtKey.CreateFromBytes(extended).PrivateKey;
            var address = key.PubKey.GetAddress(type == AddressType.P2Tr ? ScriptPubKeyType.TaprootBIP86 : ScriptPubKeyType.Segwit,
                Network.RegTest);
            var outpoint = new OutPoint(RandomUtils.GetUInt256(), index);
            var model = new UtxoModel(outpoint.Hash.ToBytes(), index, LightningMoney.Satoshis(75_000), 100,
                new WalletAddressModel(type, index, false, address.ToString()));
            _utxos.Add(model);
            if (reservation is { } id)
                Assert.True(_utxos.TryReserveForFee([(model.TxId, model.Index)], id));
            return (model, outpoint, new TxOut(Money.Satoshis(75_000), address.ScriptPubKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(extended);
        }
    }

    private CompactPubKey DepositPubKey(uint index)
    {
        byte[] extended = _keys.GetDepositP2WpkhKeyAtIndex(index, false);
        try
        {
            using var key = ExtKey.CreateFromBytes(extended).PrivateKey;
            return key.PubKey.ToBytes();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(extended);
        }
    }

    private static Transaction CreateSpend(params OutPoint[] outpoints)
    {
        var tx = Network.RegTest.CreateTransaction();
        foreach (var outpoint in outpoints)
            tx.Inputs.Add(new TxIn(outpoint));
        using var destination = new Key();
        tx.Outputs.Add(Money.Satoshis(1_000), destination.PubKey.WitHash.ScriptPubKey);
        return tx;
    }

    private static SignedTransaction ToSigned(Transaction tx) => new(tx.GetHash().ToBytes(), tx.ToBytes());

    private void AssertWipedSignerScalars()
    {
        Assert.NotEmpty(_signingKeys.ReturnedScalars);
        Assert.All(_signingKeys.ReturnedScalars, scalar => Assert.All(scalar, value => Assert.Equal(0, value)));
    }

    private sealed class TrackingSignerKeys(SecureKeyManager inner) : Domain.Protocol.Interfaces.ISecureKeyManager
    {
        public List<byte[]> ReturnedScalars { get; } = [];
        public byte[] GetSilentPaymentSpendKey(ReadOnlySpan<byte> tweak32, uint? label)
        {
            var scalar = inner.GetSilentPaymentSpendKey(tweak32, label);
            ReturnedScalars.Add(scalar);
            return scalar;
        }
        public BitcoinKeyPath ChannelKeyPath => inner.ChannelKeyPath;
        public uint HeightOfBirth => inner.HeightOfBirth;
        public ExtPrivKey GetNextChannelKey(out uint index) => inner.GetNextChannelKey(out index);
        public ExtPrivKey GetChannelKeyAtIndex(uint index) => inner.GetChannelKeyAtIndex(index);
        public ExtPrivKey GetDepositP2TrKeyAtIndex(uint index, bool isChange) => inner.GetDepositP2TrKeyAtIndex(index, isChange);
        public ExtPrivKey GetDepositP2WpkhKeyAtIndex(uint index, bool isChange) => inner.GetDepositP2WpkhKeyAtIndex(index, isChange);
        public CryptoKeyPair GetNodeKeyPair() => inner.GetNodeKeyPair();
        public CompactPubKey GetNodePubKey() => inner.GetNodePubKey();
        public void ComputeNodeSharedSecret(ReadOnlySpan<byte> publicKey, Span<byte> sharedSecret) => inner.ComputeNodeSharedSecret(publicKey, sharedSecret);
    }

    public void Dispose()
    {
        _keys.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}