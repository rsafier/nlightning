using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.Crypto;

namespace NLightning.Infrastructure.Bitcoin.Tests.Builders;

using Bitcoin.Builders;
using Bitcoin.Outputs;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Closing;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;

/// <summary>
/// BOLT 3 legacy closing transaction (B3-LCTX-01, B2-CLS-05; BOLT2 plan N10-T2). BOLT 3 has no closing-transaction
/// test vector, so the fields are checked one by one and the signed transaction is verified by NBitcoin's script
/// interpreter with real keys.
/// </summary>
public class ClosingTransactionBuilderTests
{
    private static readonly byte[] s_fundingTxId =
        Convert.FromHexString("8984484a580b825b9972d7adb15050b3ab624ccd731946b3eeddb92f4e7ef6be");

    private readonly ClosingTransactionBuilder _builder = new(new OptionsWrapper<NodeOptions>(new NodeOptions()));

    [Fact]
    public void Given_Model_When_Build_Then_Bolt3FieldsAreSet()
    {
        // Arrange
        var (local, remote) = (new Key(), new Key());
        var funding = CreateFunding(local.PubKey, remote.PubKey, 1_000_000);
        var model = LegacyClosingTransactionFactory.Create(funding, 600_000_000, 400_000_000, true, 1_000,
                                                           P2Wpkh(0x11), P2Wsh(0x22), 546);

        // Act
        var result = _builder.Build(model);

        // Assert
        var tx = Transaction.Load(result.RawTxBytes, Network.Main);
        Assert.Equal(2U, tx.Version);
        Assert.Equal(0U, (uint)tx.LockTime);
        var input = Assert.Single(tx.Inputs);
        Assert.Equal(new uint256(s_fundingTxId), input.PrevOut.Hash);
        Assert.Equal(1U, input.PrevOut.N);
        Assert.Equal(0xFFFFFFFFU, (uint)input.Sequence);
        Assert.Empty(input.ScriptSig.ToBytes());
        Assert.Equal(2, tx.Outputs.Count);
        Assert.Equal(Money.Satoshis(400_000), tx.Outputs[0].Value);
        Assert.Equal((byte[])P2Wsh(0x22), tx.Outputs[0].ScriptPubKey.ToBytes());
        Assert.Equal(Money.Satoshis(599_000), tx.Outputs[1].Value);
        Assert.Equal((byte[])P2Wpkh(0x11), tx.Outputs[1].ScriptPubKey.ToBytes());
        Assert.Equal(tx.GetHash().ToBytes(), (byte[])result.TxId);
    }

    [Fact]
    public void Given_EqualAmounts_When_Build_Then_OrderedByScriptWithShorterPrefixFirst()
    {
        // Arrange: same amount, the P2WPKH script is a prefix-compatible shorter script (both start 00 14 / 00 20)
        var (local, remote) = (new Key(), new Key());
        var funding = CreateFunding(local.PubKey, remote.PubKey, 1_000_000);
        var model = new ClosingTransactionModel(funding, LightningMoney.Satoshis(0),
        [
            new ClosingOutput(P2Wsh(0x00), LightningMoney.Satoshis(500_000), true),
            new ClosingOutput(P2Wpkh(0x00), LightningMoney.Satoshis(500_000), false)
        ]);

        // Act
        var tx = Transaction.Load(_builder.Build(model).RawTxBytes, Network.Main);

        // Assert: 0014... < 0020... (memcmp on the common prefix)
        Assert.Equal((byte[])P2Wpkh(0x00), tx.Outputs[0].ScriptPubKey.ToBytes());
        Assert.Equal((byte[])P2Wsh(0x00), tx.Outputs[1].ScriptPubKey.ToBytes());
    }

    [Fact]
    public void Given_DustOutput_When_Build_Then_SingleOutput()
    {
        // Arrange
        var (local, remote) = (new Key(), new Key());
        var funding = CreateFunding(local.PubKey, remote.PubKey, 1_000_000);
        var model = LegacyClosingTransactionFactory.Create(funding, 999_700_000, 300_000, true, 500, P2Wpkh(0x11),
                                                           P2Wpkh(0x22), 354);

        // Act
        var tx = Transaction.Load(_builder.Build(model).RawTxBytes, Network.Main);

        // Assert
        var output = Assert.Single(tx.Outputs);
        Assert.Equal(Money.Satoshis(999_200), output.Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Given_BothSignatures_When_AddWitness_Then_WitnessInFundingKeyOrderAndScriptVerifies(bool swapKeys)
    {
        // Arrange
        var keys = new[] { new Key(), new Key() };
        var (local, remote) = swapKeys ? (keys[1], keys[0]) : (keys[0], keys[1]);
        var funding = CreateFunding(local.PubKey, remote.PubKey, 1_000_000);
        var model = LegacyClosingTransactionFactory.Create(funding, 700_000_000, 300_000_000, false, 700,
                                                           P2Wpkh(0x11), P2Wpkh(0x22), 546);
        var unsigned = _builder.Build(model);
        var fundingOutput = new FundingOutput(funding.Amount, local.PubKey, remote.PubKey);
        var spent = fundingOutput.ToTxOut();
        var tx = Transaction.Load(unsigned.RawTxBytes, Network.Main);
        var sighash = tx.GetSignatureHash(fundingOutput.RedeemScript, 0, SigHash.All, spent, HashVersion.WitnessV0);
        var localSig = new CompactSignature(local.Sign(sighash).MakeCanonical().ToCompact());
        var remoteSig = new CompactSignature(remote.Sign(sighash).MakeCanonical().ToCompact());

        // Act
        var signed = _builder.AddWitness(unsigned, funding, localSig, remoteSig);

        // Assert
        var signedTx = Transaction.Load(signed.RawTxBytes, Network.Main);
        var witness = signedTx.Inputs[0].WitScript;
        Assert.Equal(4, witness.PushCount);
        Assert.Empty(witness[0]);
        var firstKey = PubKeyComparer.Instance.Compare(local.PubKey, remote.PubKey) < 0 ? local : remote;
        var firstSig = firstKey == local ? localSig : remoteSig;
        Assert.Equal(new TransactionSignature(ParseCompact(firstSig), SigHash.All).ToBytes(),
                     witness[1]);
        Assert.Equal(fundingOutput.RedeemScript.ToBytes(), witness[3]);
        Assert.True(signedTx.Inputs.AsIndexedInputs().First().VerifyScript(spent, out var error), error.ToString());
        Assert.Equal(unsigned.TxId, signed.TxId);
    }

    [Fact]
    public void Given_SignatureOverOtherVariant_When_Verified_Then_Fails()
    {
        // Arrange: signatures over the two-output variant do not verify on the variant without the remote output
        var (local, remote) = (new Key(), new Key());
        var funding = CreateFunding(local.PubKey, remote.PubKey, 1_000_000);
        var full = _builder.Build(LegacyClosingTransactionFactory.Create(funding, 700_000_000, 300_000_000, true,
                                                                         700, P2Wpkh(0x11), P2Wpkh(0x22), 546));
        var trimmed = _builder.Build(LegacyClosingTransactionFactory.Create(funding, 700_000_000, 300_000_000, true,
                                                                            700, P2Wpkh(0x11), P2Wpkh(0x22), 546,
                                                                            ClosingVariant.WithoutRemoteOutput));
        var fundingOutput = new FundingOutput(funding.Amount, local.PubKey, remote.PubKey);
        var spent = fundingOutput.ToTxOut();
        var fullTx = Transaction.Load(full.RawTxBytes, Network.Main);
        var sighash = fullTx.GetSignatureHash(fundingOutput.RedeemScript, 0, SigHash.All, spent,
                                              HashVersion.WitnessV0);
        var localSig = new CompactSignature(local.Sign(sighash).MakeCanonical().ToCompact());
        var remoteSig = new CompactSignature(remote.Sign(sighash).MakeCanonical().ToCompact());

        // Act
        var signedTrimmed = Transaction.Load(_builder.AddWitness(trimmed, funding, localSig, remoteSig).RawTxBytes,
                                             Network.Main);

        // Assert
        Assert.NotEqual(full.TxId, trimmed.TxId);
        Assert.False(signedTrimmed.Inputs.AsIndexedInputs().First().VerifyScript(spent, out _));
    }

    private static ECDSASignature ParseCompact(CompactSignature signature)
    {
        Assert.True(ECDSASignature.TryParseFromCompact(signature, out var ecdsa));
        return ecdsa;
    }

    private static FundingOutputInfo CreateFunding(PubKey local, PubKey remote, long sats) =>
        new(LightningMoney.Satoshis(sats), new CompactPubKey(local.ToBytes()), new CompactPubKey(remote.ToBytes()),
            new Domain.Bitcoin.ValueObjects.TxId(s_fundingTxId), 1);

    private static BitcoinScript P2Wpkh(byte fill) =>
        new([0x00, 0x14, .. Enumerable.Repeat(fill, 20)]);

    private static BitcoinScript P2Wsh(byte fill) =>
        new([0x00, 0x20, .. Enumerable.Repeat(fill, 32)]);
}