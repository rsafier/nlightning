using System.Numerics;
using NBitcoin;
using NBitcoin.Crypto;

namespace NLightning.Integration.Tests.BOLT3;

using Domain.Bitcoin.Transactions.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Mocks;
using Vectors;

/// <summary>
/// HTLC signatures through <c>ILightningSigner</c> (NL-057, BOLT2 plan N3-T1) against BOLT 3 Appendix C and F.
/// As node A (the vectors' local node) the signer signs its own HTLC transactions (<c>local_htlc_signature</c>,
/// RFC 6979, byte-equal) and verifies the peer's (<c>remote_htlc_signature</c>); as node B it produces the
/// <c>remote_htlc_signature</c> list of node A's commitment, in commitment output order.
/// </summary>
public class Bolt3HtlcSignerVectorTests
{
    // secp256k1 group order
    private static readonly BigInteger s_curveOrder =
        BigInteger.Parse("0FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141",
                         System.Globalization.NumberStyles.HexNumber);

    public static TheoryData<string> AppendixCNames => Bolt3SpecVectors.AppendixCNames;
    public static TheoryData<string> AppendixFNames => Bolt3SpecVectors.AppendixFNames;

    [Theory]
    [MemberData(nameof(AppendixCNames))]
    public void Given_AppendixCVector_When_SigningLocalHtlcTxs_Then_SignaturesEqualLocalHtlcSignatures(string name)
    {
        // Arrange
        var vector = Bolt3SpecVectors.GetAppendixC(name);
        var harness = new Bolt3VectorHarness(vector, false);
        var contexts = BuildContexts(harness);

        // Act
        var signatures = contexts.Select(c => harness.Signer.SignLocalHtlcTransaction(ChannelId.Zero, c)).ToList();

        // Assert
        Assert.Equal(vector.HtlcTxs.Select(h => h.LocalSigHex), signatures.Select(ToDerHex));
    }

    [Theory]
    [MemberData(nameof(AppendixCNames))]
    public void Given_AppendixC_RemoteHtlcSig_Then_Valid(string name)
    {
        // Arrange
        var vector = Bolt3SpecVectors.GetAppendixC(name);
        var harness = new Bolt3VectorHarness(vector, false);
        var contexts = BuildContexts(harness);
        var signatures = vector.HtlcTxs.Select(h => FromDerHex(h.RemoteSigHex)).ToList();

        // Act
        var exception =
            Record.Exception(() => harness.Signer.ValidateLocalHtlcSignatures(ChannelId.Zero, contexts, signatures));

        // Assert
        Assert.Null(exception);
    }

    [Theory]
    [MemberData(nameof(AppendixCNames))]
    public void Given_AppendixC_When_SigningRemoteHtlcTxs_Then_SignaturesInVectorOrder(string name)
    {
        // Arrange - node B signs node A's commitment (its remote commitment 42)
        var vector = Bolt3SpecVectors.GetAppendixC(name);
        var harness = new Bolt3VectorHarness(vector, false, true);
        var contexts = BuildContexts(harness);

        // Act
        var signatures = harness.Signer.SignRemoteHtlcTransactions(ChannelId.Zero, contexts);

        // Assert
        Assert.Equal(vector.HtlcTxs.Select(h => h.RemoteSigHex), signatures.Select(ToDerHex));
    }

    [Fact]
    public void Given_AppendixCSignaturesOutOfOrder_When_ValidatingRemoteHtlcSigs_Then_Throws()
    {
        // Arrange
        var vector = Bolt3SpecVectors.AppendixC.First(v => v.HtlcTxs.Count >= 2);
        var harness = new Bolt3VectorHarness(vector, false);
        var contexts = BuildContexts(harness);
        var signatures = vector.HtlcTxs.Select(h => FromDerHex(h.RemoteSigHex)).ToList();
        (signatures[0], signatures[1]) = (signatures[1], signatures[0]);

        // Act / Assert
        Assert.Throws<SignerException>(() => harness.Signer.ValidateLocalHtlcSignatures(ChannelId.Zero, contexts,
                                                                                       signatures));
    }

    [Fact]
    public void Given_TamperedRemoteHtlcSig_When_Validating_Then_Throws()
    {
        // Arrange
        var vector = Bolt3SpecVectors.AppendixC.First(v => v.HtlcTxs.Count > 0);
        var harness = new Bolt3VectorHarness(vector, false);
        var contexts = BuildContexts(harness);
        var signatures = vector.HtlcTxs.Select(h => FromDerHex(h.RemoteSigHex)).ToList();
        var tampered = ((byte[])signatures[0]).ToArray();
        tampered[10] ^= 0x01;
        signatures[0] = new CompactSignature(tampered);

        // Act / Assert
        Assert.Throws<SignerException>(() => harness.Signer.ValidateLocalHtlcSignatures(ChannelId.Zero, contexts,
                                                                                       signatures));
    }

    [Fact]
    public void Given_HighSRemoteHtlcSig_When_Validating_Then_Throws()
    {
        // Arrange - (r, n - s) verifies mathematically but is the malleated high-S form, which BOLT 2 forbids
        var vector = Bolt3SpecVectors.AppendixC.First(v => v.HtlcTxs.Count > 0);
        var harness = new Bolt3VectorHarness(vector, false);
        var contexts = BuildContexts(harness);
        var signatures = vector.HtlcTxs.Select(h => FromDerHex(h.RemoteSigHex)).ToList();
        signatures[0] = ToHighS(signatures[0]);

        // Act
        var exception = Assert.Throws<SignerException>(
            () => harness.Signer.ValidateLocalHtlcSignatures(ChannelId.Zero, contexts, signatures));

        // Assert
        Assert.Contains("low S", exception.Message);
    }

    [Fact]
    public void Given_MissingRemoteHtlcSig_When_Validating_Then_Throws()
    {
        // Arrange
        var vector = Bolt3SpecVectors.AppendixC.First(v => v.HtlcTxs.Count > 0);
        var harness = new Bolt3VectorHarness(vector, false);
        var contexts = BuildContexts(harness);
        var signatures = vector.HtlcTxs.Skip(1).Select(h => FromDerHex(h.RemoteSigHex)).ToList();

        // Act / Assert
        Assert.Throws<SignerException>(() => harness.Signer.ValidateLocalHtlcSignatures(ChannelId.Zero, contexts,
                                                                                       signatures));
    }

    [Theory]
    [MemberData(nameof(AppendixFNames))]
    public void Given_AppendixFVector_When_ValidatingRemoteHtlcSigs_Then_ValidOnlyAsSingleAnyoneCanPay(string name)
    {
        // Arrange
        var vector = Bolt3SpecVectors.GetAppendixF(name);
        var harness = new Bolt3VectorHarness(vector, true);
        var contexts = BuildContexts(harness);
        var withoutAnchors = contexts.Select(c => c with { HasAnchors = false }).ToList();
        var signatures = vector.HtlcTxs.Select(h => FromDerHex(h.RemoteSigHex)).ToList();

        // Act
        var exception =
            Record.Exception(() => harness.Signer.ValidateLocalHtlcSignatures(ChannelId.Zero, contexts, signatures));

        // Assert
        Assert.Null(exception);
        if (signatures.Count > 0)
            Assert.Throws<SignerException>(() => harness.Signer.ValidateLocalHtlcSignatures(
                                               ChannelId.Zero, withoutAnchors, signatures));
    }

    [Theory]
    [MemberData(nameof(AppendixFNames))]
    public void Given_AppendixFVector_When_SigningLocalHtlcTxs_Then_SignaturesEqualWitnessSignatures(string name)
    {
        // Arrange
        var vector = Bolt3SpecVectors.GetAppendixF(name);
        var harness = new Bolt3VectorHarness(vector, true);
        var contexts = BuildContexts(harness);

        // Act
        var signatures = contexts.Select(c => harness.Signer.SignLocalHtlcTransaction(ChannelId.Zero, c)).ToList();

        // Assert - the local signature is the third witness item (0, remote, local, ...), always SIGHASH_ALL
        var expected = vector.HtlcTxs
                             .Select(h => Convert.ToHexString(Transaction.Parse(h.TxHex, Network.Main).Inputs[0]
                                                                         .WitScript[2]));
        var actual = signatures.Select(s => Convert.ToHexString(
                                           new TransactionSignature(ToEcdsa(s), SigHash.All).ToBytes()));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(AppendixFNames))]
    public void Given_AppendixFAsNodeB_When_SigningRemoteHtlcTxs_Then_SingleAnyoneCanPaySigsInVectorOrder(string name)
    {
        // Arrange
        var vector = Bolt3SpecVectors.GetAppendixF(name);
        var harness = new Bolt3VectorHarness(vector, true, true);
        var contexts = BuildContexts(harness);

        // Act
        var signatures = harness.Signer.SignRemoteHtlcTransactions(ChannelId.Zero, contexts);

        // Assert
        Assert.Equal(vector.HtlcTxs.Select(h => h.RemoteSigHex), signatures.Select(ToDerHex));
    }

    private static List<HtlcSigningContext> BuildContexts(Bolt3VectorHarness harness)
    {
        var (_, htlcModels) = harness.BuildHtlcModels();
        return htlcModels.Select(m => new HtlcSigningContext(harness.HtlcBuilder.Build(m),
                                                             Bolt3TestCommitmentKeyDerivationService
                                                                .LocalPerCommitmentPoint, harness.HasAnchors))
                         .ToList();
    }

    private static ECDSASignature ToEcdsa(CompactSignature signature)
    {
        Assert.True(ECDSASignature.TryParseFromCompact(signature, out var ecdsa));
        return ecdsa;
    }

    private static string ToDerHex(CompactSignature signature) =>
        Convert.ToHexString(ToEcdsa(signature).ToDER()).ToLowerInvariant();

    private static CompactSignature FromDerHex(string derHex) =>
        new ECDSASignature(Convert.FromHexString(derHex)).ToCompact();

    private static CompactSignature ToHighS(CompactSignature signature)
    {
        var bytes = ((byte[])signature).ToArray();
        var s = new BigInteger(bytes.AsSpan(32, 32), true, true);
        var highS = (s_curveOrder - s).ToByteArray(true, true);
        Array.Clear(bytes, 32, 32);
        highS.CopyTo(bytes, 64 - highS.Length);
        return new CompactSignature(bytes);
    }
}