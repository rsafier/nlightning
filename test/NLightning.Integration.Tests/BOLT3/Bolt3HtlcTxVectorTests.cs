using NBitcoin;
using NBitcoin.Crypto;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Integration.Tests.BOLT3;

using Domain.Bitcoin.Transactions.Enums;
using Vectors;

/// <summary>
/// BOLT 3 Appendix C HTLC-timeout/HTLC-success transactions (NL-056): for every commitment vector, each HTLC
/// transaction is built from the commitment's HTLC output map, both signatures are checked against the spec
/// (<c>remote_htlc_signature</c> verifies, our RFC 6979 <c>local_htlc_signature</c> is byte-equal), the witness is added
/// with the preimage, and the signed transaction must equal the spec byte for byte.
/// </summary>
public class Bolt3HtlcTxVectorTests
{
    public static TheoryData<string> AppendixCNames => Bolt3SpecVectors.AppendixCNames;

    [Theory]
    [MemberData(nameof(AppendixCNames))]
    public void Given_AppendixCVector_When_BuildingHtlcTransactions_Then_SignedTxsMatchByteForByte(string name)
    {
        // Arrange
        var vector = Bolt3SpecVectors.GetAppendixC(name);
        var harness = new Bolt3VectorHarness(vector, false);
        var remoteHtlcPubKey = Bolt3AppendixCVectors.NodeBHtlcPubkey;

        // Act
        var (commitment, htlcModels) = harness.BuildHtlcModels();

        // Assert
        Assert.Equal(vector.HtlcTxs.Count, htlcModels.Count);
        for (var i = 0; i < htlcModels.Count; i++)
        {
            var expected = vector.HtlcTxs[i];
            var model = htlcModels[i];
            Assert.Equal(commitment.Transaction.TxId, model.CommitmentTxId);
            Assert.Equal((uint)expected.OutputIndex, model.CommitmentOutputIndex);
            Assert.Equal(expected.IsSuccess!.Value ? HtlcTransactionType.Success : HtlcTransactionType.Timeout,
                         model.Type);

            var built = harness.HtlcBuilder.Build(model);

            var remoteSignature = new ECDSASignature(Convert.FromHexString(expected.RemoteSigHex));
            Assert.True(remoteHtlcPubKey.Verify(Bolt3VectorHarness.HtlcSigHash(built, SigHash.All), remoteSignature),
                        $"remote_htlc_signature for output #{expected.OutputIndex} does not verify");
            var localSignature = Bolt3VectorHarness.SignLocalHtlc(built);
            Assert.Equal(expected.LocalSigHex, Convert.ToHexString(localSignature.ToDER()).ToLowerInvariant());

            var preimage = model.Type == HtlcTransactionType.Success
                               ? Bolt3VectorHarness.Preimages[expected.HtlcId!.Value]
                               : null;
            var signed = harness.HtlcBuilder.AddWitness(model, built, remoteSignature.ToCompact(),
                                                        localSignature.ToCompact(), preimage);
            Assert.Equal(expected.TxHex, Convert.ToHexString(signed.RawTxBytes).ToLowerInvariant());
        }
    }

    [Fact]
    public void Given_AppendixCVectors_When_Counting_Then_ThirtyThreeHtlcTransactionsAreCovered()
    {
        // Guards the loader: Appendix C lists 0+5+5+4+4+3+3+2+2+1+1+0+0+0+0+3 HTLC transactions
        Assert.Equal(33, Bolt3SpecVectors.AppendixC.Sum(v => v.HtlcTxs.Count));
    }
}