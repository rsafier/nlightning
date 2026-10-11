using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Builders;

using Bitcoin.Builders;
using Bitcoin.Outputs;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;

/// <summary>
/// BOLT 3 "Closing Transaction" of <c>option_simple_close</c> (B3-CLTX-01, B2-SC-C08; BOLT2 plan N11-T2): version 2,
/// the lock time from <c>closing_complete</c>, sequence 0xFFFFFFFD, BIP 69 outputs, an <c>OP_RETURN</c> output at zero.
/// BOLT 3 has no vector for it, so the fields are checked one by one and the signed transaction is executed by NBitcoin's
/// script interpreter with real keys.
/// </summary>
public class SimpleClosingTransactionBuilderTests
{
    private static readonly byte[] s_fundingTxId =
        Convert.FromHexString("8984484a580b825b9972d7adb15050b3ab624ccd731946b3eeddb92f4e7ef6be");

    private readonly ClosingTransactionBuilder _builder = new(new OptionsWrapper<NodeOptions>(new NodeOptions()));

    [Fact]
    public void Given_SimpleClose_Then_Seq0xFFFFFFFD_LocktimeFromMsg_Bip69()
    {
        // Arrange: the closer (local) pays 1,000 sat
        var (local, remote) = (new Key(), new Key());
        var funding = CreateFunding(local.PubKey, remote.PubKey);
        var model = new ClosingTransactionModel(funding, LightningMoney.Satoshis(1_000),
        [
            new ClosingOutput(P2Wpkh(0x11), LightningMoney.Satoshis(599_000), true),
            new ClosingOutput(P2Wsh(0x22), LightningMoney.Satoshis(400_000), false)
        ]);

        // Act
        var result = _builder.BuildSimple(model, 812_345);

        // Assert
        var tx = Transaction.Load(result.RawTxBytes, Network.Main);
        Assert.Equal(2U, tx.Version);
        Assert.Equal(812_345U, (uint)tx.LockTime);
        var input = Assert.Single(tx.Inputs);
        Assert.Equal(new uint256(s_fundingTxId), input.PrevOut.Hash);
        Assert.Equal(1U, input.PrevOut.N);
        Assert.Equal(ClosingTransactionBuilder.SimpleCloseSequence, (uint)input.Sequence);
        Assert.Equal(0xFFFFFFFDU, (uint)input.Sequence);
        Assert.Empty(input.ScriptSig.ToBytes());
        Assert.Equal(Money.Satoshis(400_000), tx.Outputs[0].Value);
        Assert.Equal(Money.Satoshis(599_000), tx.Outputs[1].Value);
        Assert.Equal(tx.GetHash().ToBytes(), (byte[])result.TxId);
    }

    [Fact]
    public void Given_OpReturnOutput_When_BuildSimple_Then_ZeroAmountFirst()
    {
        // Arrange: BOLT 3: an OP_RETURN output carries 0
        var (local, remote) = (new Key(), new Key());
        BitcoinScript opReturn = new([0x6a, 0x06, 1, 2, 3, 4, 5, 6]);
        var model = new ClosingTransactionModel(CreateFunding(local.PubKey, remote.PubKey),
                                                LightningMoney.Satoshis(600_000),
        [
            new ClosingOutput(opReturn, LightningMoney.Zero, true),
            new ClosingOutput(P2Wpkh(0x22), LightningMoney.Satoshis(400_000), false)
        ]);

        // Act
        var tx = Transaction.Load(_builder.BuildSimple(model, 0).RawTxBytes, Network.Main);

        // Assert
        Assert.Equal(Money.Zero, tx.Outputs[0].Value);
        Assert.Equal((byte[])opReturn, tx.Outputs[0].ScriptPubKey.ToBytes());
        Assert.True(tx.Outputs[0].ScriptPubKey.IsUnspendable);
    }

    [Fact]
    public void Given_BothSignatures_When_AddWitness_Then_ScriptVerifiesAndOtherLockTimeDoesNot()
    {
        // Arrange
        var (local, remote) = (new Key(), new Key());
        var funding = CreateFunding(local.PubKey, remote.PubKey);
        var model = new ClosingTransactionModel(funding, LightningMoney.Satoshis(700),
        [
            new ClosingOutput(P2Wpkh(0x11), LightningMoney.Satoshis(699_300), true),
            new ClosingOutput(P2Wpkh(0x22), LightningMoney.Satoshis(300_000), false)
        ]);
        var unsigned = _builder.BuildSimple(model, 500);
        var other = _builder.BuildSimple(model, 501);
        var fundingOutput = new FundingOutput(funding.Amount, local.PubKey, remote.PubKey);
        var spent = fundingOutput.ToTxOut();
        var sighash = Transaction.Load(unsigned.RawTxBytes, Network.Main)
                                 .GetSignatureHash(fundingOutput.RedeemScript, 0, SigHash.All, spent,
                                                   HashVersion.WitnessV0);
        var localSig = new CompactSignature(local.Sign(sighash).MakeCanonical().ToCompact());
        var remoteSig = new CompactSignature(remote.Sign(sighash).MakeCanonical().ToCompact());

        // Act
        var signed = Transaction.Load(_builder.AddWitness(unsigned, funding, localSig, remoteSig).RawTxBytes,
                                      Network.Main);
        var signedOther = Transaction.Load(_builder.AddWitness(other, funding, localSig, remoteSig).RawTxBytes,
                                           Network.Main);

        // Assert: the lock time is signed, so a signature is only valid for the closing_complete's lock time
        Assert.True(signed.Inputs.AsIndexedInputs().First().VerifyScript(spent, out var error), error.ToString());
        Assert.False(signedOther.Inputs.AsIndexedInputs().First().VerifyScript(spent, out _));
        Assert.NotEqual(unsigned.TxId, _builder.Build(model).TxId);
    }

    private static FundingOutputInfo CreateFunding(PubKey local, PubKey remote) =>
        new(LightningMoney.Satoshis(1_000_000), new CompactPubKey(local.ToBytes()), new CompactPubKey(remote.ToBytes()),
            new TxId(s_fundingTxId), 1);

    private static BitcoinScript P2Wpkh(byte fill) =>
        new([0x00, 0x14, .. Enumerable.Repeat(fill, 20)]);

    private static BitcoinScript P2Wsh(byte fill) =>
        new([0x00, 0x20, .. Enumerable.Repeat(fill, 32)]);
}