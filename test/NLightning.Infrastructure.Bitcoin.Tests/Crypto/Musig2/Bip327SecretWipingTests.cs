using System.Security.Cryptography;

namespace NLightning.Infrastructure.Bitcoin.Tests.Crypto.Musig2;

using Domain.Crypto.Constants;
using Domain.Exceptions;
using Infrastructure.Bitcoin.Crypto.Musig2;

/// <summary>
/// NL-911: the BIP 327 steps that absorb a secret (<c>rand'</c>, the secret key, <c>sk'</c>) hash it with a
/// <see cref="WipingSha256"/> that is wiped before the call returns or throws, and Sign zeroes the secret nonce's
/// scalars on every path. Every hasher a call makes is recorded through <see cref="WipingSha256.Tracker"/> (this test's
/// async flow only).
/// </summary>
public class Bip327SecretWipingTests
{
    private static readonly byte[] s_secretKey = Enumerable.Repeat((byte)0x11, 32).ToArray();
    private static readonly byte[] s_otherSecretKey = Enumerable.Repeat((byte)0x22, 32).ToArray();

    [Fact]
    public void Given_NonceGenWithASecretKey_When_ItReturns_Then_EverySecretHasherWasWiped()
    {
        // Arrange
        var hashers = Track();
        var pubKey = Bip327.IndividualPubKey(s_secretKey);

        // Act
        var (secNonce, _) = Bip327.NonceGen(RandomNumberGenerator.GetBytes(32), pubKey, s_secretKey, null, [1, 2],
                                            null);

        // Assert: MuSig/aux and the two MuSig/nonce hashes
        Assert.Equal(3, hashers.Count);
        Assert.All(hashers, h => Assert.True(h.IsWiped));
        Assert.Equal(MusigConstants.SecretNonceLen, secNonce.Length);
    }

    [Fact]
    public void Given_DeterministicSign_When_ItReturns_Then_EverySecretHasherWasWiped()
    {
        // Arrange
        var (pubKeys, otherNonce) = TwoSigners();
        var hashers = Track();

        // Act
        Bip327.DeterministicSign(s_secretKey, otherNonce, pubKeys, [], [7, 7, 7], RandomNumberGenerator.GetBytes(32));

        // Assert: MuSig/aux and the two MuSig/deterministic/nonce hashes
        Assert.Equal(3, hashers.Count);
        Assert.All(hashers, h => Assert.True(h.IsWiped));
    }

    [Fact]
    public void Given_DeterministicSignWithAnInvalidOtherNonce_When_ItThrows_Then_EverySecretHasherWasWiped()
    {
        // Arrange: the other nonce fails to decode only after the secret nonce hashes ran
        var (pubKeys, _) = TwoSigners();
        var badNonce = new byte[MusigConstants.AggregateNonceLen];
        badNonce[0] = 0x04;
        var hashers = Track();

        // Act
        Assert.Throws<MusigInvalidContributionException>(
            () => Bip327.DeterministicSign(s_secretKey, badNonce, pubKeys, [], [7], null));

        // Assert
        Assert.Equal(2, hashers.Count);
        Assert.All(hashers, h => Assert.True(h.IsWiped));
    }

    [Fact]
    public void Given_SignWithAnotherSecretKey_When_ItThrows_Then_TheSecretNonceScalarsAreZeroed()
    {
        // Arrange
        var (pubKeys, otherNonce) = TwoSigners();
        var pubKey = Bip327.IndividualPubKey(s_secretKey);
        var (secNonce, pubNonce) = Bip327.NonceGen(RandomNumberGenerator.GetBytes(32), pubKey, null, null, null, null);
        var session = new Bip327.SessionContext(Bip327.NonceAgg([pubNonce, otherNonce]), pubKeys, [], [9]);

        // Act
        Assert.Throws<MusigException>(() => Bip327.Sign(secNonce, s_otherSecretKey, session));

        // Assert: the scalars are gone, the public key half stays (as the reference)
        Assert.All(secNonce[..MusigConstants.SecretNonceScalarsLen], b => Assert.Equal(0, b));
        Assert.Equal(pubKey, secNonce[MusigConstants.SecretNonceScalarsLen..]);
    }

    [Fact]
    public void Given_Sign_When_ItReturns_Then_TheSecretNonceScalarsAreZeroed()
    {
        // Arrange
        var (pubKeys, otherNonce) = TwoSigners();
        var pubKey = Bip327.IndividualPubKey(s_secretKey);
        var (secNonce, pubNonce) = Bip327.NonceGen(RandomNumberGenerator.GetBytes(32), pubKey, null, null, null, null);
        var session = new Bip327.SessionContext(Bip327.NonceAgg([pubNonce, otherNonce]), pubKeys, [], [9]);

        // Act
        var partial = Bip327.Sign(secNonce, s_secretKey, session);

        // Assert
        Assert.Equal(MusigConstants.PartialSignatureLen, partial.Length);
        Assert.All(secNonce[..MusigConstants.SecretNonceScalarsLen], b => Assert.Equal(0, b));
        Assert.True(Bip327.PartialSigVerifyInternal(partial, pubNonce, pubKey, session));
    }

    private static List<WipingSha256> Track()
    {
        var hashers = new List<WipingSha256>();
        WipingSha256.Tracker.Value = hashers;
        return hashers;
    }

    private static (List<byte[]> PubKeys, byte[] OtherNonce) TwoSigners()
    {
        var otherPubKey = Bip327.IndividualPubKey(s_otherSecretKey);
        var (_, otherNonce) = Bip327.NonceGen(RandomNumberGenerator.GetBytes(32), otherPubKey, null, null, null, null);
        return (Bip327.KeySort([Bip327.IndividualPubKey(s_secretKey), otherPubKey]), otherNonce);
    }
}