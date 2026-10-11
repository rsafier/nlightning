using NBitcoin;
using NBitcoin.Crypto;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Integration.Tests.BOLT3;

using Domain.Bitcoin.Transactions.Enums;
using Domain.Money;
using Vectors;

/// <summary>
/// BOLT 3 Appendix F (option_anchors): every commitment transaction and every HTLC transaction, built from the spec's
/// balances, feerate, dust limit and HTLC set, must equal the spec byte for byte once signed. HTLC transactions have
/// sequence 1 and zero fee, and the remote HTLC signature commits with <c>SIGHASH_SINGLE|SIGHASH_ANYONECANPAY</c>.
/// </summary>
public class Bolt3AnchorVectorTests
{
    public static TheoryData<string> AppendixFNames => Bolt3SpecVectors.AppendixFNames;

    [Fact]
    public void Given_AppendixFFile_When_Loaded_Then_AllNineVectorsArePresent()
    {
        // Act
        var vectors = Bolt3SpecVectors.AppendixF;

        // Assert - 5+4+2+1+0+0+3 HTLC transactions
        Assert.Equal(9, vectors.Count);
        Assert.Equal(15, vectors.Sum(v => v.HtlcTxs.Count));
    }

    [Theory]
    [MemberData(nameof(AppendixFNames))]
    public void Given_AppendixFVector_When_BuildingFromSpec_Then_SignedCommitmentTxMatchesByteForByte(string name)
    {
        // Arrange
        var vector = Bolt3SpecVectors.GetAppendixF(name);
        var harness = new Bolt3VectorHarness(vector, true);

        // Act
        var model = harness.CreateCommitmentModel();
        var unsignedCommitment = harness.CommitmentBuilder.Build(model);
        var signedCommitment = harness.SignCommitment(unsignedCommitment);

        // Assert
        Assert.Equal(vector.CommitTxHex, signedCommitment.ToHex());
    }

    [Theory]
    [MemberData(nameof(AppendixFNames))]
    public void Given_AppendixFVector_When_BuildingHtlcTransactions_Then_SignedTxsMatchByteForByte(string name)
    {
        // Arrange
        var vector = Bolt3SpecVectors.GetAppendixF(name);
        var harness = new Bolt3VectorHarness(vector, true);
        var remoteHtlcPubKey = Bolt3AppendixCVectors.NodeBHtlcPubkey;

        // Act
        var (commitment, htlcModels) = harness.BuildHtlcModels();

        // Assert
        Assert.Equal(vector.HtlcTxs.Count, htlcModels.Count);
        for (var i = 0; i < htlcModels.Count; i++)
        {
            var expected = vector.HtlcTxs[i];
            var expectedTx = Transaction.Parse(expected.TxHex, Network.Main);
            var model = htlcModels[i];
            Assert.Equal(commitment.Transaction.TxId, model.CommitmentTxId);
            Assert.Equal((uint)expected.OutputIndex, model.CommitmentOutputIndex);
            Assert.Equal(LightningMoney.Zero, model.Fee);
            Assert.Equal(1U, (uint)model.Sequence);

            var built = harness.HtlcBuilder.Build(model);

            // The remote signature only verifies over SIGHASH_SINGLE|SIGHASH_ANYONECANPAY
            var remoteSignature = new ECDSASignature(Convert.FromHexString(expected.RemoteSigHex));
            var anyoneCanPayHash = Bolt3VectorHarness.HtlcSigHash(built, SigHash.Single | SigHash.AnyoneCanPay);
            Assert.True(remoteHtlcPubKey.Verify(anyoneCanPayHash, remoteSignature));
            Assert.False(remoteHtlcPubKey.Verify(Bolt3VectorHarness.HtlcSigHash(built, SigHash.All), remoteSignature));

            // Our signature (SIGHASH_ALL) equals the one in the spec's witness
            var localSignature = Bolt3VectorHarness.SignLocalHtlc(built);
            Assert.Equal(Convert.ToHexString(expectedTx.Inputs[0].WitScript[2]),
                         Convert.ToHexString(new TransactionSignature(localSignature, SigHash.All).ToBytes()));

            var preimage = model.Type == HtlcTransactionType.Success
                               ? Bolt3VectorHarness.Preimages[model.SpentOutput.Htlc.Id]
                               : null;
            var signed = harness.HtlcBuilder.AddWitness(model, built, remoteSignature.ToCompact(),
                                                        localSignature.ToCompact(), preimage);
            Assert.Equal(expected.TxHex, Convert.ToHexString(signed.RawTxBytes).ToLowerInvariant());
        }
    }
}