using NBitcoin.Secp256k1;

namespace NLightning.Infrastructure.Bitcoin.Tests.Crypto.Musig2;

using Domain.Crypto.Models;
using Domain.Crypto.ValueObjects;
using Infrastructure.Bitcoin.Crypto.Musig2;
using static Musig2VectorKit;

/// <summary>
/// BIP 327 <c>sign_verify_vectors.json</c>, <c>tweak_vectors.json</c>, <c>sig_agg_vectors.json</c> and
/// <c>det_sign_vectors.json</c>, every case, through the BIP 327 module and <see cref="Musig2Service"/>, as the BIP's
/// reference test runner checks them.
/// </summary>
public class Bip327SignVectorTests
{
    private const string SignVerifyFile = "sign_verify_vectors.json";
    private const string TweakFile = "tweak_vectors.json";
    private const string SigAggFile = "sig_agg_vectors.json";
    private const string DetSignFile = "det_sign_vectors.json";

    private readonly Musig2Service _service = new();

    public static TheoryData<int> SignVerifyValidCases => CaseIndexes(SignVerifyFile, "valid_test_cases");
    public static TheoryData<int> SignErrorCases => CaseIndexes(SignVerifyFile, "sign_error_test_cases");
    public static TheoryData<int> VerifyFailCases => CaseIndexes(SignVerifyFile, "verify_fail_test_cases");
    public static TheoryData<int> VerifyErrorCases => CaseIndexes(SignVerifyFile, "verify_error_test_cases");
    public static TheoryData<int> TweakValidCases => CaseIndexes(TweakFile, "valid_test_cases");
    public static TheoryData<int> TweakErrorCases => CaseIndexes(TweakFile, "error_test_cases");
    public static TheoryData<int> SigAggValidCases => CaseIndexes(SigAggFile, "valid_test_cases");
    public static TheoryData<int> SigAggErrorCases => CaseIndexes(SigAggFile, "error_test_cases");
    public static TheoryData<int> DetSignValidCases => CaseIndexes(DetSignFile, "valid_test_cases");
    public static TheoryData<int> DetSignErrorCases => CaseIndexes(DetSignFile, "error_test_cases");

    #region sign_verify_vectors.json

    [Fact]
    public void Given_SignVerifyVector_When_Checked_Then_ItsFixturesAreConsistent()
    {
        // Arrange
        var vector = Load(SignVerifyFile);
        var secretKey = Hex(vector.GetProperty("sk"));
        var pubKeys = HexArray(vector.GetProperty("pubkeys"));
        var secNonce = HexArray(vector.GetProperty("secnonces"))[0];
        var pubNonces = HexArray(vector.GetProperty("pnonces"));
        var aggNonces = HexArray(vector.GetProperty("aggnonces"));

        // Act / Assert: the reference runner's own preconditions
        Assert.Equal(pubKeys[0], Bip327.IndividualPubKey(secretKey));
        Assert.Equal(pubNonces[0], PublicNonceOf(secNonce));
        Assert.Equal(aggNonces[0], Bip327.NonceAgg([pubNonces[0], pubNonces[1], pubNonces[2]]));
        Assert.Equal(aggNonces[1], Bip327.NonceAgg([pubNonces[0], pubNonces[3]]));
    }

    [Theory]
    [InlineData("000000000000000000000000000000000000000000000000000000000000000000"
                + "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798",
                "1d83b7bf6a91c616f04bbf62a60ca78408aacf935291daf40c6387c3c09980f5")]
    [InlineData("0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798"
                + "000000000000000000000000000000000000000000000000000000000000000000",
                "8b2a3d94fab3567f1677e8b967ca64e6749ed889a1e17458a8174596c4823ab7")]
    public void Given_AnAggregateNonceWithOneInfinityHalf_When_Signed_Then_PartialSignatureEqualsReferenceAndVerifies(
        string aggNonceHex, string expectedHex)
    {
        // Arrange: the sign_verify fixture (sk, secnonce 0, keys 0-2, msg 0) over an aggregate nonce with one half
        // at infinity (33 zero bytes); expected values from BIP 327's reference.py sign()
        var vector = Load(SignVerifyFile);
        var secretKey = Hex(vector.GetProperty("sk"));
        var pubKeys = HexArray(vector.GetProperty("pubkeys"))[..3];
        var msg = HexArray(vector.GetProperty("msgs"))[0];
        var secNonce = HexArray(vector.GetProperty("secnonces"))[0];
        var aggNonce = Convert.FromHexString(aggNonceHex);
        var session = new Bip327.SessionContext(aggNonce, pubKeys, [], msg);

        // Act
        var partialSig = Bip327.Sign((byte[])secNonce.Clone(), secretKey, session);

        // Assert
        Assert.Equal(Convert.FromHexString(expectedHex), partialSig);
        Assert.True(Bip327.PartialSigVerifyInternal(partialSig, PublicNonceOf(secNonce), pubKeys[0], session));
    }

    [Theory]
    [MemberData(nameof(SignVerifyValidCases))]
    public void Given_SignVerifyValidCase_When_Signed_Then_PartialSignatureEqualsExpectedAndVerifies(int index)
    {
        // Arrange
        var vector = Load(SignVerifyFile);
        var testCase = Case(SignVerifyFile, "valid_test_cases", index);
        var secretKey = Hex(vector.GetProperty("sk"));
        var pubKeys = Pick(HexArray(vector.GetProperty("pubkeys")), Ints(testCase.GetProperty("key_indices")));
        var pubNonces = Pick(HexArray(vector.GetProperty("pnonces")), Ints(testCase.GetProperty("nonce_indices")));
        var aggNonce = HexArray(vector.GetProperty("aggnonces"))[testCase.GetProperty("aggnonce_index").GetInt32()];
        var msg = HexArray(vector.GetProperty("msgs"))[testCase.GetProperty("msg_index").GetInt32()];
        var signerIndex = testCase.GetProperty("signer_index").GetInt32();
        var expected = Hex(testCase.GetProperty("expected"));
        var secNonce = HexArray(vector.GetProperty("secnonces"))[0];
        var session = new Bip327.SessionContext(aggNonce, pubKeys, [], msg);
        var domainSession = new MusigSigningSession(ToPubKeys(pubKeys), [], aggNonce, msg);

        // Act
        var partialSig = Bip327.Sign((byte[])secNonce.Clone(), secretKey, session);
        var partialSigByService = _service.Sign(new MusigSecretNonce(secNonce), secretKey, domainSession);

        // Assert
        Assert.Equal(aggNonce, Bip327.NonceAgg(pubNonces));
        Assert.Equal(expected, partialSig);
        Assert.Equal(expected, (byte[])partialSigByService);
        Assert.True(Bip327.PartialSigVerify(expected, pubNonces, pubKeys, [], msg, signerIndex));
        Assert.True(_service.VerifyPartialSignature(expected, pubNonces[signerIndex], pubKeys[signerIndex],
                                                    domainSession));
    }

    [Theory]
    [MemberData(nameof(SignErrorCases))]
    public void Given_SignErrorCase_When_Signed_Then_FailsAsTheVectorSays(int index)
    {
        // Arrange
        var vector = Load(SignVerifyFile);
        var testCase = Case(SignVerifyFile, "sign_error_test_cases", index);
        var secretKey = Hex(vector.GetProperty("sk"));
        var pubKeys = Pick(HexArray(vector.GetProperty("pubkeys")), Ints(testCase.GetProperty("key_indices")));
        var aggNonce = HexArray(vector.GetProperty("aggnonces"))[testCase.GetProperty("aggnonce_index").GetInt32()];
        var msg = HexArray(vector.GetProperty("msgs"))[testCase.GetProperty("msg_index").GetInt32()];
        var secNonce = HexArray(vector.GetProperty("secnonces"))[testCase.GetProperty("secnonce_index").GetInt32()];
        var session = new Bip327.SessionContext(aggNonce, pubKeys, [], msg);
        var domainSession = new MusigSigningSession(ToPubKeys(pubKeys), [], aggNonce, msg);

        // Act / Assert
        AssertError(testCase.GetProperty("error"), () => Bip327.Sign((byte[])secNonce.Clone(), secretKey, session));
        AssertError(testCase.GetProperty("error"),
                    () => _service.Sign(new MusigSecretNonce(secNonce), secretKey, domainSession));
    }

    [Theory]
    [MemberData(nameof(VerifyFailCases))]
    public void Given_VerifyFailCase_When_Verified_Then_False(int index)
    {
        // Arrange
        var vector = Load(SignVerifyFile);
        var testCase = Case(SignVerifyFile, "verify_fail_test_cases", index);
        var sig = Hex(testCase.GetProperty("sig"));
        var pubKeys = Pick(HexArray(vector.GetProperty("pubkeys")), Ints(testCase.GetProperty("key_indices")));
        var pubNonces = Pick(HexArray(vector.GetProperty("pnonces")), Ints(testCase.GetProperty("nonce_indices")));
        var msg = HexArray(vector.GetProperty("msgs"))[testCase.GetProperty("msg_index").GetInt32()];
        var signerIndex = testCase.GetProperty("signer_index").GetInt32();
        var domainSession = new MusigSigningSession(ToPubKeys(pubKeys), [], Bip327.NonceAgg(pubNonces), msg);

        // Act
        var valid = Bip327.PartialSigVerify(sig, pubNonces, pubKeys, [], msg, signerIndex);
        var validByService = _service.VerifyPartialSignature(sig, pubNonces[signerIndex], pubKeys[signerIndex],
                                                             domainSession);

        // Assert
        Assert.False(valid);
        Assert.False(validByService);
    }

    [Theory]
    [MemberData(nameof(VerifyErrorCases))]
    public void Given_VerifyErrorCase_When_Verified_Then_FailsAsTheVectorSays(int index)
    {
        // Arrange
        var vector = Load(SignVerifyFile);
        var testCase = Case(SignVerifyFile, "verify_error_test_cases", index);
        var sig = Hex(testCase.GetProperty("sig"));
        var pubKeys = Pick(HexArray(vector.GetProperty("pubkeys")), Ints(testCase.GetProperty("key_indices")));
        var pubNonces = Pick(HexArray(vector.GetProperty("pnonces")), Ints(testCase.GetProperty("nonce_indices")));
        var msg = HexArray(vector.GetProperty("msgs"))[testCase.GetProperty("msg_index").GetInt32()];
        var signerIndex = testCase.GetProperty("signer_index").GetInt32();

        // Act / Assert: the service verifies within a session, so the nonces are aggregated first as the BIP's
        // PartialSigVerify does
        AssertError(testCase.GetProperty("error"),
                    () => Bip327.PartialSigVerify(sig, pubNonces, pubKeys, [], msg, signerIndex));
        AssertError(testCase.GetProperty("error"), () =>
        {
            var aggNonce = _service.AggregateNonces(pubNonces.Select(n => (MusigPublicNonce)n).ToArray());
            var session = new MusigSigningSession(ToPubKeys(pubKeys), [], aggNonce, msg);
            _service.VerifyPartialSignature(sig, pubNonces[signerIndex], pubKeys[signerIndex], session);
        });
    }

    #endregion

    #region tweak_vectors.json

    [Fact]
    public void Given_TweakVector_When_Checked_Then_ItsFixturesAreConsistent()
    {
        // Arrange
        var vector = Load(TweakFile);
        var pubNonces = HexArray(vector.GetProperty("pnonces"));

        // Act / Assert
        Assert.Equal(HexArray(vector.GetProperty("pubkeys"))[0], Bip327.IndividualPubKey(Hex(vector.GetProperty("sk"))));
        Assert.Equal(pubNonces[0], PublicNonceOf(Hex(vector.GetProperty("secnonce"))));
        Assert.Equal(Hex(vector.GetProperty("aggnonce")), Bip327.NonceAgg([pubNonces[0], pubNonces[1], pubNonces[2]]));
    }

    [Theory]
    [MemberData(nameof(TweakValidCases))]
    public void Given_TweakValidCase_When_Signed_Then_PartialSignatureEqualsExpectedAndVerifies(int index)
    {
        // Arrange
        var vector = Load(TweakFile);
        var testCase = Case(TweakFile, "valid_test_cases", index);
        var secretKey = Hex(vector.GetProperty("sk"));
        var pubKeys = Pick(HexArray(vector.GetProperty("pubkeys")), Ints(testCase.GetProperty("key_indices")));
        var pubNonces = Pick(HexArray(vector.GetProperty("pnonces")), Ints(testCase.GetProperty("nonce_indices")));
        var tweaks = Tweaks(HexArray(vector.GetProperty("tweaks")), testCase);
        var aggNonce = Hex(vector.GetProperty("aggnonce"));
        var msg = Hex(vector.GetProperty("msg"));
        var secNonce = Hex(vector.GetProperty("secnonce"));
        var signerIndex = testCase.GetProperty("signer_index").GetInt32();
        var expected = Hex(testCase.GetProperty("expected"));
        var session = new Bip327.SessionContext(aggNonce, pubKeys, tweaks, msg);
        var domainSession = new MusigSigningSession(ToPubKeys(pubKeys), ToMusigTweaks(tweaks), aggNonce, msg);

        // Act
        var partialSig = Bip327.Sign((byte[])secNonce.Clone(), secretKey, session);
        var partialSigByService = _service.Sign(new MusigSecretNonce(secNonce), secretKey, domainSession);

        // Assert
        Assert.Equal(expected, partialSig);
        Assert.Equal(expected, (byte[])partialSigByService);
        Assert.True(Bip327.PartialSigVerify(expected, pubNonces, pubKeys, tweaks, msg, signerIndex));
        Assert.True(_service.VerifyPartialSignature(expected, pubNonces[signerIndex], pubKeys[signerIndex],
                                                    domainSession));
    }

    [Theory]
    [MemberData(nameof(TweakErrorCases))]
    public void Given_TweakErrorCase_When_Signed_Then_FailsAsTheVectorSays(int index)
    {
        // Arrange
        var vector = Load(TweakFile);
        var testCase = Case(TweakFile, "error_test_cases", index);
        var secretKey = Hex(vector.GetProperty("sk"));
        var pubKeys = Pick(HexArray(vector.GetProperty("pubkeys")), Ints(testCase.GetProperty("key_indices")));
        var tweaks = Tweaks(HexArray(vector.GetProperty("tweaks")), testCase);
        var aggNonce = Hex(vector.GetProperty("aggnonce"));
        var msg = Hex(vector.GetProperty("msg"));
        var secNonce = Hex(vector.GetProperty("secnonce"));
        var session = new Bip327.SessionContext(aggNonce, pubKeys, tweaks, msg);
        var secretNonce = new MusigSecretNonce(secNonce);

        // Act / Assert
        AssertError(testCase.GetProperty("error"), () => Bip327.Sign((byte[])secNonce.Clone(), secretKey, session));
        AssertError(testCase.GetProperty("error"), () => _service.Sign(
                        secretNonce, secretKey,
                        new MusigSigningSession(ToPubKeys(pubKeys), ToMusigTweaks(tweaks), aggNonce, msg)));

        // An invalid session never consumes the secret nonce
        Assert.False(secretNonce.IsUsed);
    }

    #endregion

    #region sig_agg_vectors.json

    [Theory]
    [MemberData(nameof(SigAggValidCases))]
    public void Given_SigAggValidCase_When_Aggregated_Then_SignatureEqualsExpectedAndVerifies(int index)
    {
        // Arrange
        var vector = Load(SigAggFile);
        var testCase = Case(SigAggFile, "valid_test_cases", index);
        var pubKeys = Pick(HexArray(vector.GetProperty("pubkeys")), Ints(testCase.GetProperty("key_indices")));
        var pubNonces = Pick(HexArray(vector.GetProperty("pnonces")), Ints(testCase.GetProperty("nonce_indices")));
        var tweaks = Tweaks(HexArray(vector.GetProperty("tweaks")), testCase);
        var partialSigs = Pick(HexArray(vector.GetProperty("psigs")), Ints(testCase.GetProperty("psig_indices")));
        var aggNonce = Hex(testCase.GetProperty("aggnonce"));
        var msg = Hex(vector.GetProperty("msg"));
        var expected = Hex(testCase.GetProperty("expected"));
        var domainSession = new MusigSigningSession(ToPubKeys(pubKeys), ToMusigTweaks(tweaks), aggNonce, msg);

        // Act
        var signature = Bip327.PartialSigAgg(partialSigs, new Bip327.SessionContext(aggNonce, pubKeys, tweaks, msg));
        var signatureByService = _service.AggregatePartialSignatures(
            partialSigs.Select(s => (MusigPartialSignature)s).ToArray(), domainSession);

        // Assert
        Assert.Equal(aggNonce, Bip327.NonceAgg(pubNonces));
        Assert.Equal(expected, signature);
        Assert.Equal(expected, signatureByService);
        var aggPubKey = Bip327.GetXOnlyPubKey(Bip327.KeyAggAndTweak(pubKeys, tweaks));
        Assert.True(Bip327.SchnorrVerify(msg, aggPubKey, signature));
        Assert.True(_service.VerifySignature(signature, aggPubKey, msg));
        Assert.True(SecpSchnorrSignature.TryCreate(signature, out var parsed));
        Assert.True(ECXOnlyPubKey.Create(aggPubKey).SigVerifyBIP340(parsed!, msg));
    }

    [Theory]
    [MemberData(nameof(SigAggErrorCases))]
    public void Given_SigAggErrorCase_When_Aggregated_Then_FailsAsTheVectorSays(int index)
    {
        // Arrange
        var vector = Load(SigAggFile);
        var testCase = Case(SigAggFile, "error_test_cases", index);
        var pubKeys = Pick(HexArray(vector.GetProperty("pubkeys")), Ints(testCase.GetProperty("key_indices")));
        var pubNonces = Pick(HexArray(vector.GetProperty("pnonces")), Ints(testCase.GetProperty("nonce_indices")));
        var tweaks = Tweaks(HexArray(vector.GetProperty("tweaks")), testCase);
        var partialSigs = Pick(HexArray(vector.GetProperty("psigs")), Ints(testCase.GetProperty("psig_indices")));
        var msg = Hex(vector.GetProperty("msg"));
        var aggNonce = Bip327.NonceAgg(pubNonces);

        // Act / Assert
        AssertError(testCase.GetProperty("error"),
                    () => Bip327.PartialSigAgg(partialSigs, new Bip327.SessionContext(aggNonce, pubKeys, tweaks, msg)));
        AssertError(testCase.GetProperty("error"), () => _service.AggregatePartialSignatures(
                        partialSigs.Select(s => (MusigPartialSignature)s).ToArray(),
                        new MusigSigningSession(ToPubKeys(pubKeys), ToMusigTweaks(tweaks), aggNonce, msg)));
    }

    #endregion

    #region det_sign_vectors.json

    [Theory]
    [MemberData(nameof(DetSignValidCases))]
    public void Given_DetSignValidCase_When_Signed_Then_NonceAndPartialSignatureEqualExpected(int index)
    {
        // Arrange
        var vector = Load(DetSignFile);
        var testCase = Case(DetSignFile, "valid_test_cases", index);
        var secretKey = Hex(vector.GetProperty("sk"));
        var pubKeys = Pick(HexArray(vector.GetProperty("pubkeys")), Ints(testCase.GetProperty("key_indices")));
        var aggOtherNonce = Hex(testCase.GetProperty("aggothernonce"));
        var tweaks = HexArray(testCase.GetProperty("tweaks")).Zip(Bools(testCase.GetProperty("is_xonly")))
                                                              .Select(t => (t.First, t.Second)).ToArray();
        var msg = HexArray(vector.GetProperty("msgs"))[testCase.GetProperty("msg_index").GetInt32()];
        var signerIndex = testCase.GetProperty("signer_index").GetInt32();
        var rand = HexOrNull(testCase.GetProperty("rand"));
        var expected = HexArray(testCase.GetProperty("expected"));

        // Act
        var (pubNonce, partialSig) = Bip327.DeterministicSign(secretKey, aggOtherNonce, pubKeys, tweaks, msg, rand);
        var byService = _service.DeterministicSign(secretKey, aggOtherNonce, ToPubKeys(pubKeys),
                                                   ToMusigTweaks(tweaks), msg, rand);

        // Assert
        Assert.Equal(expected[0], pubNonce);
        Assert.Equal(expected[1], partialSig);
        Assert.Equal(expected[0], (byte[])byService.PublicNonce);
        Assert.Equal(expected[1], (byte[])byService.PartialSignature);
        var session = new Bip327.SessionContext(Bip327.NonceAgg([aggOtherNonce, pubNonce]), pubKeys, tweaks, msg);
        Assert.True(Bip327.PartialSigVerifyInternal(partialSig, pubNonce, pubKeys[signerIndex], session));
    }

    [Theory]
    [MemberData(nameof(DetSignErrorCases))]
    public void Given_DetSignErrorCase_When_Signed_Then_FailsAsTheVectorSays(int index)
    {
        // Arrange
        var vector = Load(DetSignFile);
        var testCase = Case(DetSignFile, "error_test_cases", index);
        var secretKey = Hex(vector.GetProperty("sk"));
        var pubKeys = Pick(HexArray(vector.GetProperty("pubkeys")), Ints(testCase.GetProperty("key_indices")));
        var aggOtherNonce = Hex(testCase.GetProperty("aggothernonce"));
        var tweaks = HexArray(testCase.GetProperty("tweaks")).Zip(Bools(testCase.GetProperty("is_xonly")))
                                                              .Select(t => (t.First, t.Second)).ToArray();
        var msg = HexArray(vector.GetProperty("msgs"))[testCase.GetProperty("msg_index").GetInt32()];
        var rand = HexOrNull(testCase.GetProperty("rand"));

        // Act / Assert
        AssertError(testCase.GetProperty("error"),
                    () => Bip327.DeterministicSign(secretKey, aggOtherNonce, pubKeys, tweaks, msg, rand));
        AssertError(testCase.GetProperty("error"),
                    () => _service.DeterministicSign(secretKey, aggOtherNonce, ToPubKeys(pubKeys),
                                                     ToMusigTweaks(tweaks), msg, rand));
    }

    #endregion

    /// <summary>
    /// <c>k1*G || k2*G</c> of a 97-byte secret nonce, through NBitcoin directly (not the module under test).
    /// </summary>
    internal static byte[] PublicNonceOf(byte[] secNonce)
    {
        var r1 = ECPrivKey.Create(secNonce.AsSpan(0, 32)).CreatePubKey().ToBytes(true);
        var r2 = ECPrivKey.Create(secNonce.AsSpan(32, 32)).CreatePubKey().ToBytes(true);
        return [.. r1, .. r2];
    }
}