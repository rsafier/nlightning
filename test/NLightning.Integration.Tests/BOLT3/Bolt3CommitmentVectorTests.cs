using NBitcoin;

namespace NLightning.Integration.Tests.BOLT3;

using Vectors;

/// <summary>
/// BOLT 3 Appendix C commitment transactions, built from each vector's <c>to_local_msat</c>, <c>to_remote_msat</c>,
/// <c>local_feerate_per_kw</c> and HTLC set through a <c>CommitmentTxSpec</c>, and compared byte for byte with the
/// spec's fully signed <c>output commit_tx</c> after inserting both signatures.
/// </summary>
public class Bolt3CommitmentVectorTests
{
    public static TheoryData<string> AppendixCNames => Bolt3SpecVectors.AppendixCNames;

    [Fact]
    public void Given_AppendixCFile_When_Loaded_Then_AllSixteenVectorsArePresent()
    {
        // Act
        var vectors = Bolt3SpecVectors.AppendixC;

        // Assert
        Assert.Equal(16, vectors.Count);
        Assert.All(vectors, v => Assert.Equal(v.HtlcTxs.Count, v.HtlcTxs.Select(h => h.OutputIndex).Distinct().Count()));
    }

    [Theory]
    [MemberData(nameof(AppendixCNames))]
    public void Given_AppendixCVector_When_BuildingFromSpec_Then_SignedCommitmentTxMatchesByteForByte(string name)
    {
        // Arrange
        var vector = Bolt3SpecVectors.GetAppendixC(name);
        var harness = new Bolt3VectorHarness(vector, false);

        // Act
        var model = harness.CreateCommitmentModel();
        var unsignedCommitment = harness.CommitmentBuilder.Build(model);
        var signedCommitment = harness.SignCommitment(unsignedCommitment);

        // Assert
        Assert.Equal(vector.CommitTxHex, signedCommitment.ToHex());
        var expectedLocalSignature = Transaction.Parse(vector.CommitTxHex, Network.Main).Inputs[0].WitScript[1];
        Assert.Equal(vector.LocalSigHex + "01", Convert.ToHexString(expectedLocalSignature).ToLowerInvariant());
    }

    [Theory]
    [MemberData(nameof(AppendixCNames))]
    public void Given_AppendixCVector_When_BuildingWithOutputMap_Then_HtlcOutputsAreInSpecOrder(string name)
    {
        // Arrange
        var vector = Bolt3SpecVectors.GetAppendixC(name);
        var harness = new Bolt3VectorHarness(vector, false);

        // Act
        var result = harness.CommitmentBuilder.BuildWithOutputMap(harness.CreateCommitmentModel());

        // Assert - one entry per HTLC transaction vector, same output index and HTLC, in transaction order
        Assert.Equal(vector.HtlcTxs.Select(h => ((ulong)h.HtlcId!.Value, (uint)h.OutputIndex)),
                     result.HtlcOutputsInTxOrder.Select(h => (h.Output.Htlc.Id, h.Vout)));
    }
}