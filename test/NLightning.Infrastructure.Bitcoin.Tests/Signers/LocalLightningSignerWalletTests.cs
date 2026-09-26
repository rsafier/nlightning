using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Signers;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Signers;
using Wallet;

/// <summary>
/// <see cref="LocalLightningSigner.SignWalletTransaction(SignedTransaction)"/> (BOLT 5 plan O7-T1, NL-067 second half):
/// every signature is checked here with NBitcoin's script interpreter, independently of the signer's own check.
/// </summary>
public class LocalLightningSignerWalletTests
{
    private static readonly ExtKey s_masterKey =
        ExtKey.CreateFromSeed(Convert.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"));

    private readonly FakeWalletUtxoRepository _utxos = new();
    private readonly LocalLightningSigner _signer;
    private readonly Mock<ISecureKeyManager> _keyManager = new();

    public LocalLightningSignerWalletTests()
    {
        _keyManager.Setup(k => k.GetDepositP2WpkhKeyAtIndex(It.IsAny<uint>(), It.IsAny<bool>()))
                   .Returns((uint index, bool isChange) => GetP2WpkhExtKey(index, isChange).ToBytes());
        _keyManager.Setup(k => k.GetDepositP2TrKeyAtIndex(It.IsAny<uint>(), It.IsAny<bool>()))
                   .Returns((uint index, bool isChange) => GetP2TrExtKey(index, isChange).ToBytes());

        _signer = new LocalLightningSigner(Mock.Of<IFundingOutputBuilder>(), Mock.Of<IKeyDerivationService>(),
                                           NullLogger<LocalLightningSigner>.Instance,
                                           new NodeOptions { BitcoinNetwork = NetworkConstants.Regtest },
                                           _keyManager.Object, _utxos);
    }

    [Fact]
    public void Given_ReservedP2WpkhInputs_When_Signing_Then_EveryInputVerifies()
    {
        // Arrange
        var first = AddWalletUtxo(AddressType.P2Wpkh, 0, false, 60_000, reserved: true);
        var second = AddWalletUtxo(AddressType.P2Wpkh, 4, true, 40_000, reserved: true);
        var tx = CreateSpend(first.OutPoint, second.OutPoint);
        var signed = ToSigned(tx);

        // Act
        var allSigned = _signer.SignWalletTransaction(signed);

        // Assert
        Assert.True(allSigned);
        var result = Transaction.Parse(Convert.ToHexString(signed.RawTxBytes), Network.RegTest);
        Assert.Equal(tx.GetHash(), result.GetHash());
        AssertInputVerifies(result, 0, first.TxOut);
        AssertInputVerifies(result, 1, second.TxOut);
        AssertAllVerify(result, first.TxOut, second.TxOut);
    }

    [Fact]
    public void Given_ReservedP2TrInput_When_Signing_Then_TheKeyPathSpendVerifies()
    {
        // Arrange
        var utxo = AddWalletUtxo(AddressType.P2Tr, 2, false, 75_000, reserved: true);
        var tx = CreateSpend(utxo.OutPoint);
        var signed = ToSigned(tx);

        // Act
        var allSigned = _signer.SignWalletTransaction(signed);

        // Assert
        Assert.True(allSigned);
        var result = Transaction.Parse(Convert.ToHexString(signed.RawTxBytes), Network.RegTest);
        Assert.Single(result.Inputs[0].WitScript.Pushes);
        AssertAllVerify(result, utxo.TxOut);
    }

    [Fact]
    public void Given_ForeignAnchorInputAndWalletInputs_When_Signing_Then_WalletInputsVerifyAndTheForeignOneIsUntouched()
    {
        // Arrange: a CPFP child: the anchor (a P2WSH output of a commitment, signed by the channel signer) plus a
        // P2WPKH and a P2TR fee input
        var anchor = new TxOut(Money.Satoshis(330), new Script(OpcodeType.OP_1).WitHash.ScriptPubKey);
        var anchorOutPoint = new OutPoint(RandomUtils.GetUInt256(), 0);
        var p2Wpkh = AddWalletUtxo(AddressType.P2Wpkh, 1, false, 20_000, reserved: true);
        var p2Tr = AddWalletUtxo(AddressType.P2Tr, 1, true, 30_000, reserved: true);
        var tx = CreateSpend(anchorOutPoint, p2Wpkh.OutPoint, p2Tr.OutPoint);
        var signed = ToSigned(tx);
        SpentOutput[] others =
        [
            new(new TxId(anchorOutPoint.Hash.ToBytes()), anchorOutPoint.N, LightningMoney.Satoshis(330),
                anchor.ScriptPubKey.ToBytes())
        ];

        // Act
        var allSigned = _signer.SignWalletTransaction(signed, others);

        // Assert
        Assert.True(allSigned);
        var result = Transaction.Parse(Convert.ToHexString(signed.RawTxBytes), Network.RegTest);
        Assert.Empty(result.Inputs[0].WitScript.Pushes);
        var validator = result.CreateValidator([anchor, p2Wpkh.TxOut, p2Tr.TxOut]);
        Assert.True(validator.ValidateInput(1).Error is null or ScriptError.OK);
        Assert.True(validator.ValidateInput(2).Error is null or ScriptError.OK);
        Assert.False(validator.ValidateInput(0).Error is null or ScriptError.OK);
    }

    [Fact]
    public void Given_ForeignInputAndP2WpkhInput_When_SignedWithoutTheForeignOutput_Then_TheWalletInputVerifies()
    {
        // Arrange: BIP 143 signs one input's own amount, so the other spent outputs are not needed
        var foreign = new OutPoint(RandomUtils.GetUInt256(), 3);
        var p2Wpkh = AddWalletUtxo(AddressType.P2Wpkh, 7, false, 50_000, reserved: true);
        var tx = CreateSpend(foreign, p2Wpkh.OutPoint);
        var signed = ToSigned(tx);

        // Act
        var allSigned = _signer.SignWalletTransaction(signed);

        // Assert
        Assert.True(allSigned);
        var result = Transaction.Parse(Convert.ToHexString(signed.RawTxBytes), Network.RegTest);
        AssertInputVerifies(result, 1, p2Wpkh.TxOut);
    }

    [Fact]
    public void Given_P2TrInputAndAForeignInput_When_TheForeignOutputIsNotGiven_Then_ThrowsAndSignsNothing()
    {
        // Arrange
        var foreign = new OutPoint(RandomUtils.GetUInt256(), 0);
        var p2Tr = AddWalletUtxo(AddressType.P2Tr, 3, false, 50_000, reserved: true);
        var signed = ToSigned(CreateSpend(foreign, p2Tr.OutPoint));
        var before = signed.RawTxBytes.ToArray();

        // Act / Assert
        Assert.Throws<SignerException>(() => _signer.SignWalletTransaction(signed));
        Assert.Equal(before, signed.RawTxBytes);
    }

    [Fact]
    public void Given_AnUnreservedWalletInput_When_Signing_Then_ThrowsAndSignsNothing()
    {
        // Arrange: only reserved outputs are signed, so a spend never takes an output another spend holds
        var reserved = AddWalletUtxo(AddressType.P2Wpkh, 0, false, 60_000, reserved: true);
        var unreserved = AddWalletUtxo(AddressType.P2Wpkh, 1, false, 60_000, reserved: false);
        var signed = ToSigned(CreateSpend(reserved.OutPoint, unreserved.OutPoint));
        var before = signed.RawTxBytes.ToArray();

        // Act
        var exception = Assert.Throws<SignerException>(() => _signer.SignWalletTransaction(signed));

        // Assert
        Assert.Contains("not reserved", exception.Message);
        Assert.Equal(before, signed.RawTxBytes);
        _keyManager.Verify(k => k.GetDepositP2WpkhKeyAtIndex(It.IsAny<uint>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void Given_AWalletInputLockedToAChannelFunding_When_Signing_Then_Throws()
    {
        // Arrange
        var locked = AddWalletUtxo(AddressType.P2Wpkh, 0, false, 60_000, reserved: false);
        locked.Model.LockedToChannelId = ChannelId.Zero;
        var signed = ToSigned(CreateSpend(locked.OutPoint));

        // Act / Assert
        Assert.Throws<SignerException>(() => _signer.SignWalletTransaction(signed));
    }

    [Fact]
    public void Given_NoWalletInput_When_Signing_Then_ReturnsFalse()
    {
        // Arrange
        var signed = ToSigned(CreateSpend(new OutPoint(RandomUtils.GetUInt256(), 0)));
        var before = signed.RawTxBytes.ToArray();

        // Act
        var allSigned = _signer.SignWalletTransaction(signed);

        // Assert
        Assert.False(allSigned);
        Assert.Equal(before, signed.RawTxBytes);
    }

    [Fact]
    public void Given_ExtendedKeyBytes_When_Signing_Then_TheKeyManagerBufferIsWiped()
    {
        // Arrange: the signer zeroes the extended key bytes it gets once the key is built
        byte[]? handedOut = null;
        _keyManager.Setup(k => k.GetDepositP2WpkhKeyAtIndex(It.IsAny<uint>(), It.IsAny<bool>()))
                   .Returns((uint index, bool isChange) => handedOut = GetP2WpkhExtKey(index, isChange).ToBytes());
        var utxo = AddWalletUtxo(AddressType.P2Wpkh, 9, false, 10_000, reserved: true);
        var signed = ToSigned(CreateSpend(utxo.OutPoint));

        // Act
        Assert.True(_signer.SignWalletTransaction(signed));

        // Assert
        Assert.NotNull(handedOut);
        Assert.All(handedOut, b => Assert.Equal(0, b));
    }

    private static ExtKey GetP2WpkhExtKey(uint index, bool isChange) =>
        s_masterKey.Derive(isChange ? 1u : 0u).Derive(index);

    private static ExtKey GetP2TrExtKey(uint index, bool isChange) =>
        s_masterKey.Derive(isChange ? 3u : 2u).Derive(index);

    private (UtxoModel Model, OutPoint OutPoint, TxOut TxOut) AddWalletUtxo(AddressType type, uint index,
                                                                           bool isChange, long amountSat,
                                                                           bool reserved)
    {
        var pubKey = type == AddressType.P2Wpkh
                         ? GetP2WpkhExtKey(index, isChange).Neuter().PubKey
                         : GetP2TrExtKey(index, isChange).Neuter().PubKey;
        var address = pubKey.GetAddress(type == AddressType.P2Wpkh
                                            ? ScriptPubKeyType.Segwit
                                            : ScriptPubKeyType.TaprootBIP86, Network.RegTest);
        var outPoint = new OutPoint(RandomUtils.GetUInt256(), index);
        var model = new UtxoModel(new TxId(outPoint.Hash.ToBytes()), outPoint.N, LightningMoney.Satoshis(amountSat),
                                  100, new WalletAddressModel(type, index, isChange, address.ToString()));
        _utxos.Add(model);
        if (reserved)
            Assert.True(_utxos.TryReserveForFee([(model.TxId, model.Index)], Guid.NewGuid()));

        return (model, outPoint, new TxOut(Money.Satoshis(amountSat), address.ScriptPubKey));
    }

    private static Transaction CreateSpend(params OutPoint[] outPoints)
    {
        var tx = Network.RegTest.CreateTransaction();
        tx.Version = 2;
        foreach (var outPoint in outPoints)
            tx.Inputs.Add(new TxIn(outPoint) { Sequence = 0xFFFFFFFD });
        tx.Outputs.Add(Money.Satoshis(1_000), new Key().PubKey.WitHash.ScriptPubKey);
        return tx;
    }

    private static SignedTransaction ToSigned(Transaction tx) => new(tx.GetHash().ToBytes(), tx.ToBytes());

    private static void AssertInputVerifies(Transaction tx, int index, TxOut spent)
    {
        Assert.True(tx.Inputs.FindIndexedInput(index).VerifyScript(spent, out var error), $"input {index}: {error}");
    }

    private static void AssertAllVerify(Transaction tx, params TxOut[] spent)
    {
        var validator = tx.CreateValidator(spent);
        for (var i = 0; i < tx.Inputs.Count; i++)
        {
            var error = validator.ValidateInput(i).Error;
            Assert.True(error is null or ScriptError.OK, $"input {i}: {error}");
        }
    }
}