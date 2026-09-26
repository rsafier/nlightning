using NBitcoin;
using NBitcoin.Crypto;

namespace NLightning.Integration.Tests.BOLT3;

using Application.Channels.Services;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Mocks;
using Vectors;

/// <summary>
/// <see cref="CommitmentSigningService"/> (BOLT2 plan N3-T5) end to end against BOLT 3 Appendix C and F. Signing as
/// node B, our signature on node A's commitment and our HTLC signatures must equal the vectors'
/// <c>remote_signature</c> and <c>remote_htlc_signature</c>s in order; verifying as node A, the vectors' remote
/// signatures must be accepted for our local commitment and tampered ones rejected.
/// </summary>
public class CommitmentSigningServiceVectorTests
{
    public static TheoryData<string, bool> AllVectors
    {
        get
        {
            var data = new TheoryData<string, bool>();
            foreach (var vector in Bolt3SpecVectors.AppendixC)
                data.Add(vector.Name, false);
            foreach (var vector in Bolt3SpecVectors.AppendixF)
                data.Add(vector.Name, true);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AllVectors))]
    public void Given_Vector_When_SigningRemoteCommitmentAsNodeB_Then_SignaturesEqualTheVector(string name,
        bool hasAnchors)
    {
        // Arrange
        var vector = GetVector(name, hasAnchors);
        var harness = new Bolt3VectorHarness(vector, hasAnchors, true);
        var service = CreateService(harness);
        var expectedTxId = Transaction.Parse(vector.CommitTxHex, Network.Main).GetHash();

        // Act
        var result = service.SignRemoteCommitment(harness.Channel, harness.Spec,
                                                  harness.Channel.RemoteCommitmentNumber,
                                                  Bolt3TestCommitmentKeyDerivationService.LocalPerCommitmentPoint);

        // Assert
        Assert.Equal(expectedTxId.ToBytes(), (byte[])result.CommitmentTxId);
        Assert.Equal(vector.RemoteSigHex, ToDerHex(result.Signature));
        Assert.Equal(vector.HtlcTxs.Select(h => h.RemoteSigHex), result.HtlcSignatures.Select(ToDerHex));
    }

    [Theory]
    [MemberData(nameof(AllVectors))]
    public void Given_Vector_When_VerifyingLocalCommitmentAsNodeA_Then_VectorSignaturesAreAccepted(string name,
        bool hasAnchors)
    {
        // Arrange
        var vector = GetVector(name, hasAnchors);
        var harness = new Bolt3VectorHarness(vector, hasAnchors);
        var service = CreateService(harness);
        var signature = FromDerHex(vector.RemoteSigHex);
        var htlcSignatures = vector.HtlcTxs.Select(h => FromDerHex(h.RemoteSigHex)).ToList();
        var expectedTxId = Transaction.Parse(vector.CommitTxHex, Network.Main).GetHash();

        // Act
        var result = service.VerifyLocalCommitment(harness.Channel, harness.Spec, harness.Channel.LocalCommitmentNumber,
                                                   signature, htlcSignatures);

        // Assert
        Assert.Equal(expectedTxId.ToBytes(), (byte[])result.CommitmentTxId);
        Assert.Equal(signature, result.Signature);
        Assert.Equal(htlcSignatures, result.HtlcSignatures);
    }

    [Fact]
    public void Given_WrongCommitmentSignature_When_VerifyingLocalCommitment_Then_Throws()
    {
        // Arrange - the signature of another vector's commitment
        var vector = Bolt3SpecVectors.AppendixC[1];
        var harness = new Bolt3VectorHarness(vector, false);
        var service = CreateService(harness);
        var htlcSignatures = vector.HtlcTxs.Select(h => FromDerHex(h.RemoteSigHex)).ToList();

        // Act / Assert
        Assert.Throws<SignerException>(() => service.VerifyLocalCommitment(
                                           harness.Channel, harness.Spec, harness.Channel.LocalCommitmentNumber,
                                           FromDerHex(Bolt3SpecVectors.AppendixC[0].RemoteSigHex), htlcSignatures));
    }

    [Fact]
    public void Given_HtlcSignaturesOfAnotherCommitment_When_VerifyingLocalCommitment_Then_Throws()
    {
        // Arrange - two vectors with the same number of HTLC outputs but different feerates (different HTLC txs)
        var vector = Bolt3SpecVectors.AppendixC[1];
        var other = Bolt3SpecVectors.AppendixC.First(v => v != vector && v.HtlcTxs.Count == vector.HtlcTxs.Count);
        var harness = new Bolt3VectorHarness(vector, false);
        var service = CreateService(harness);

        // Act / Assert
        Assert.Throws<SignerException>(() => service.VerifyLocalCommitment(
                                           harness.Channel, harness.Spec, harness.Channel.LocalCommitmentNumber,
                                           FromDerHex(vector.RemoteSigHex),
                                           other.HtlcTxs.Select(h => FromDerHex(h.RemoteSigHex)).ToList()));
    }

    private static Bolt3CommitmentVector GetVector(string name, bool hasAnchors) =>
        hasAnchors ? Bolt3SpecVectors.GetAppendixF(name) : Bolt3SpecVectors.GetAppendixC(name);

    private static CommitmentSigningService CreateService(Bolt3VectorHarness harness) =>
        new(harness.Factory, harness.CommitmentBuilder, harness.HtlcBuilder, harness.Signer);

    private static string ToDerHex(CompactSignature signature)
    {
        Assert.True(ECDSASignature.TryParseFromCompact(signature, out var ecdsa));
        return Convert.ToHexString(ecdsa.ToDER()).ToLowerInvariant();
    }

    private static CompactSignature FromDerHex(string derHex) =>
        new ECDSASignature(Convert.FromHexString(derHex)).ToCompact();
}