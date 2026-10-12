using NBitcoin;
using NBitcoin.Crypto;

namespace NLightning.Application.Tests.Channels.Splicing;

using Application.Channels.Splicing;
using Domain.Crypto.ValueObjects;

/// <summary>
/// The shared funding of a splice in Bitcoin terms (BOLT 3 "Funding Transaction Output"): the P2WSH 2-of-2 script and
/// the witness of its spend, checked against NBitcoin (script, DER, and a real spend of the output verified by the
/// script interpreter).
/// </summary>
public class SpliceFundingScriptsTests
{
    private static readonly Key s_keyA = new(Enumerable.Repeat((byte)0x11, 32).ToArray());
    private static readonly Key s_keyB = new(Enumerable.Repeat((byte)0x22, 32).ToArray());

    [Fact]
    public void Given_TwoFundingKeys_When_ScriptsCreated_Then_TheyAreTheSortedMultisigAndItsP2wsh()
    {
        // Arrange
        var expectedWitnessScript = PayToMultiSigTemplate.Instance.GenerateScriptPubKey(
            2, new[] { s_keyA.PubKey, s_keyB.PubKey }.OrderBy(k => k, PubKeyComparer.Instance).ToArray());

        // Act
        var (scriptPubKey, witnessScript) = SpliceFundingScripts.Create(s_keyB.PubKey.ToBytes(), s_keyA.PubKey.ToBytes());

        // Assert
        Assert.Equal(expectedWitnessScript.ToBytes(), (byte[])witnessScript);
        Assert.Equal(expectedWitnessScript.WitHash.ScriptPubKey.ToBytes(), (byte[])scriptPubKey);
        Assert.Equal(71, ((byte[])witnessScript).Length);
    }

    [Fact]
    public void Given_BothSignatures_When_WitnessBuilt_Then_AStandardSpendOfTheFundingOutputVerifies()
    {
        // Arrange: a transaction spending the 2-of-2 output, signed by both keys (BIP 143, SIGHASH_ALL, low-S)
        var witnessScript = new Script((byte[])SpliceFundingScripts.Create(s_keyA.PubKey.ToBytes(),
                                                                           s_keyB.PubKey.ToBytes()).WitnessScript);
        var funding = Network.RegTest.CreateTransaction();
        funding.Outputs.Add(Money.Satoshis(1_000_000), witnessScript.WitHash.ScriptPubKey);
        var spend = Network.RegTest.CreateTransaction();
        spend.Inputs.Add(new OutPoint(funding, 0));
        spend.Outputs.Add(Money.Satoshis(990_000), s_keyA.PubKey.WitHash.ScriptPubKey);
        var coin = new ScriptCoin(funding, 0, witnessScript);
        var hash = spend.GetSignatureHash(witnessScript, 0, SigHash.All, coin.TxOut, HashVersion.WitnessV0);
        var signatureA = new CompactSignature(s_keyA.Sign(hash).MakeCanonical().ToCompact());
        var signatureB = new CompactSignature(s_keyB.Sign(hash).MakeCanonical().ToCompact());

        // Act: the node of key B builds it (local B, remote A)
        var witness = SpliceFundingScripts.BuildWitness(s_keyB.PubKey.ToBytes(), s_keyA.PubKey.ToBytes(),
                                                        signatureB, signatureA);
        spend.Inputs[0].WitScript = new WitScript((byte[])witness);

        // Assert
        Assert.Equal(4, spend.Inputs[0].WitScript.PushCount);
        Assert.Empty(spend.Inputs[0].WitScript[0]);
        Assert.Equal(witnessScript.ToBytes(), spend.Inputs[0].WitScript[3]);
        var validator = spend.CreateValidator([coin.TxOut]);
        Assert.True(validator.ValidateInput(0).Error is null, validator.ValidateInput(0).Error?.ToString());
        Assert.True(((byte[])witness).Length <= SpliceFundingScripts.SharedInputWitnessWeight);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x7F)]
    [InlineData(0x80)]
    [InlineData(0xFF)]
    public void Given_ACompactSignature_When_DerEncoded_Then_ItEqualsNBitcoinsStrictDer(byte first)
    {
        // Arrange: r with the edge high bytes (leading zeros, the sign bit), s low (NBitcoin makes a high s low)
        var compact = Enumerable.Repeat((byte)0x01, 64).ToArray();
        compact[0] = first;
        compact[1] = (byte)(first == 0 ? 0x00 : 0x42);
        compact[32] = first >= 0x80 ? (byte)0x7F : first;
        Assert.True(ECDSASignature.TryParseFromCompact(compact, out var expected));

        // Act
        var der = SpliceFundingScripts.ToDer(new CompactSignature(compact));

        // Assert
        Assert.Equal(expected.ToDER(), der);
        Assert.True(ECDSASignature.IsValidDER(der));
    }

    [Fact]
    public void Given_ClnsWeightOfTheSharedInput_When_WePayAsSpliceInitiator_Then_WePayForAtLeastIt()
    {
        // Arrange: NL-1292, CLN v26.06.9's accepter refused our splice-out to a P2WPKH address at 2,500 sat/kw: "Your
        // fee (1810000msat) was too low, must be at least 1812sat weight: 725". CLN counts the 2-of-2 shared input as
        // 387 wu (bitcoin_tx_input_weight: 1 + 222 + 164, the witness item count twice); we counted 386
        const int clnSharedInputWeight = 387;
        const long clnWeight = 725;

        // Act
        var ourWeight = Domain.Protocol.InteractiveTx.CollaborativeFeeCalculator.CommonFieldsWeight
                      + SpliceFundingScripts.GetSharedInputFeeWeight(false) + (8 + 1 + 34) * 4 + (8 + 1 + 22) * 4;

        // Assert
        Assert.Equal(clnSharedInputWeight, SpliceFundingScripts.SharedInputFeeWeight);
        Assert.Equal(clnWeight, ourWeight);
        Assert.Equal(SpliceFundingScripts.TaprootSharedInputWeight, SpliceFundingScripts.GetSharedInputFeeWeight(true));
    }
}