using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;
using NBitcoinPartialSignature = NBitcoin.Secp256k1.Musig.MusigPartialSignature;

namespace NLightning.Infrastructure.Bitcoin.Tests.Crypto.Musig2;

using Domain.Crypto.Enums;
using Domain.Crypto.Interfaces;
using Domain.Crypto.Models;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Infrastructure.Bitcoin.Crypto.Musig2;

/// <summary>
/// <see cref="Musig2Service"/> as the channel layer uses it: a two-party session over a BIP 86 funding key, single-use
/// secret nonces, refused partial signatures, and a cross-check of random sessions against NBitcoin.Secp256k1's own
/// MuSig2 (which verifies our partial signatures and aggregates them to the same signature).
/// </summary>
public class Musig2ServiceTests
{
    private readonly Musig2Service _service = new();

    [Fact]
    public void Given_BitcoinInfrastructure_When_ResolvingMusig2Service_Then_ReturnsSingleton()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddBitcoinInfrastructure();
        using var provider = services.BuildServiceProvider();

        // Act
        var first = provider.GetRequiredService<IMusig2Service>();
        var second = provider.GetRequiredService<IMusig2Service>();

        // Assert
        Assert.IsType<Musig2Service>(first);
        Assert.Same(first, second);
    }

    [Fact]
    public void Given_RandomTwoPartySessions_When_SignedAndAggregated_Then_NBitcoinAgreesAndTheSignatureVerifies()
    {
        for (var round = 0; round < 16; round++)
        {
            // Arrange
            var party = TwoPartySession.Create(_service, RandomNumberGenerator.GetBytes(32));

            // Act
            var signatureA = _service.Sign(party.NonceA.SecretNonce, party.KeyA, party.Session);
            var signatureB = _service.Sign(party.NonceB.SecretNonce, party.KeyB, party.Session);
            var signature = _service.AggregatePartialSignatures([signatureA, signatureB], party.Session);

            // Assert: ours
            Assert.True(_service.VerifyPartialSignature(signatureA, party.NonceA.PublicNonce, party.PubKeyA,
                                                        party.Session));
            Assert.True(_service.VerifyPartialSignature(signatureB, party.NonceB.PublicNonce, party.PubKeyB,
                                                        party.Session));
            Assert.True(_service.VerifySignature(signature, party.Aggregate.XOnlyOutputKey, party.Message));

            // Assert: NBitcoin.Secp256k1's MuSig2 over the same sorted keys, tweak and nonces
            var context = new MusigContext(party.Aggregate.PubKeys.Select(k => ECPubKey.Create(k)).ToArray(), false,
                                           party.Message, null);
            context.Tweak(party.Aggregate.Tweaks[0].Value.Span, true);
            context.ProcessNonces([new MusigPubNonce(party.NonceA.PublicNonce),
                                   new MusigPubNonce(party.NonceB.PublicNonce)]);
            Assert.Equal(party.Aggregate.OutputKey, (CompactPubKey)context.AggregatePubKey.ToBytes(true));
            Assert.Equal((byte[])party.Session.AggregateNonce, context.AggregateNonce!.ToBytes());
            Assert.True(context.Verify(ECPubKey.Create(party.PubKeyA), new MusigPubNonce(party.NonceA.PublicNonce),
                                       new NBitcoinPartialSignature(signatureA)));
            Assert.True(context.Verify(ECPubKey.Create(party.PubKeyB), new MusigPubNonce(party.NonceB.PublicNonce),
                                       new NBitcoinPartialSignature(signatureB)));
            Assert.Equal(context.AggregateSignatures([new NBitcoinPartialSignature(signatureA),
                                                      new NBitcoinPartialSignature(signatureB)]).ToBytes(), signature);
            Assert.True(ECXOnlyPubKey.Create(party.Aggregate.XOnlyOutputKey)
                                     .SigVerifyBIP340(SecpSchnorrSignature.TryCreate(signature, out var parsed)
                                                          ? parsed!
                                                          : throw new InvalidOperationException("Bad signature."),
                                                      party.Message));
        }
    }

    [Fact]
    public void Given_ASecretNonceUsedOnce_When_SignedAgain_Then_Refused()
    {
        // Arrange
        var party = TwoPartySession.Create(_service, RandomNumberGenerator.GetBytes(32));
        var otherMessage = RandomNumberGenerator.GetBytes(32);
        _service.Sign(party.NonceA.SecretNonce, party.KeyA, party.Session);

        // Act
        var exception = Assert.Throws<InvalidOperationException>(
            () => _service.Sign(party.NonceA.SecretNonce, party.KeyA, party.Session with { Message = otherMessage }));

        // Assert
        Assert.True(party.NonceA.SecretNonce.IsUsed);
        Assert.Contains("already used", exception.Message);
    }

    [Fact]
    public void Given_AClearedSecretNonce_When_Signed_Then_Refused()
    {
        // Arrange
        var party = TwoPartySession.Create(_service, RandomNumberGenerator.GetBytes(32));
        party.NonceA.SecretNonce.Dispose();

        // Act / Assert
        Assert.Throws<InvalidOperationException>(() => _service.Sign(party.NonceA.SecretNonce, party.KeyA,
                                                                      party.Session));
        Assert.True(party.NonceA.SecretNonce.IsUsed);
    }

    [Fact]
    public void Given_ASecretNonceForAnotherKey_When_Signed_Then_RefusedAndConsumed()
    {
        // Arrange
        var party = TwoPartySession.Create(_service, RandomNumberGenerator.GetBytes(32));

        // Act
        var exception = Assert.Throws<MusigException>(
            () => _service.Sign(party.NonceA.SecretNonce, party.KeyB, party.Session));

        // Assert
        Assert.Equal("Public key does not match nonce_gen argument", exception.Message);
        Assert.True(party.NonceA.SecretNonce.IsUsed);
    }

    [Fact]
    public void Given_AWrongPartialSignature_When_Verified_Then_False()
    {
        // Arrange
        var party = TwoPartySession.Create(_service, RandomNumberGenerator.GetBytes(32));
        var signatureA = (byte[])_service.Sign(party.NonceA.SecretNonce, party.KeyA, party.Session);
        var tampered = (byte[])signatureA.Clone();
        tampered[31] ^= 0x01;

        // Act
        var tamperedValid = _service.VerifyPartialSignature(tampered, party.NonceA.PublicNonce, party.PubKeyA,
                                                            party.Session);
        var otherSignerValid = _service.VerifyPartialSignature(signatureA, party.NonceB.PublicNonce, party.PubKeyB,
                                                               party.Session);
        var otherMessageValid = _service.VerifyPartialSignature(
            signatureA, party.NonceA.PublicNonce, party.PubKeyA,
            party.Session with { Message = RandomNumberGenerator.GetBytes(32) });

        // Assert
        Assert.False(tamperedValid);
        Assert.False(otherSignerValid);
        Assert.False(otherMessageValid);
    }

    [Fact]
    public void Given_APeerNonceOffTheCurve_When_Aggregated_Then_ThatPeerIsBlamed()
    {
        // Arrange
        var party = TwoPartySession.Create(_service, RandomNumberGenerator.GetBytes(32));
        var bad = (byte[])party.NonceB.PublicNonce;
        bad[33] = 0x05;

        // Act
        var exception = Assert.Throws<MusigInvalidContributionException>(
            () => _service.AggregateNonces([party.NonceA.PublicNonce, bad]));

        // Assert
        Assert.Equal(1, exception.Signer);
        Assert.Equal(MusigContribution.PubNonce, exception.Contribution);
    }

    [Fact]
    public void Given_APartialSignatureAboveTheOrder_When_Aggregated_Then_ThatSignerIsBlamed()
    {
        // Arrange
        var party = TwoPartySession.Create(_service, RandomNumberGenerator.GetBytes(32));
        var signatureA = _service.Sign(party.NonceA.SecretNonce, party.KeyA, party.Session);
        var aboveOrder = Enumerable.Repeat((byte)0xFF, 32).ToArray();

        // Act
        var exception = Assert.Throws<MusigInvalidContributionException>(
            () => _service.AggregatePartialSignatures([signatureA, aboveOrder], party.Session));

        // Assert
        Assert.Equal(1, exception.Signer);
        Assert.Equal(MusigContribution.PartialSignature, exception.Contribution);
    }

    [Fact]
    public void Given_RandomnessOfAWrongLength_When_GeneratingANonce_Then_Refused()
    {
        // Arrange
        var key = new Key();

        // Act / Assert
        Assert.Throws<MusigException>(() => _service.GenerateNonce(new byte[31], key.PubKey));
    }

    [Fact]
    public void Given_TheSameRandomness_When_GeneratingNoncesForTwoMessages_Then_TheNoncesDiffer()
    {
        // Arrange
        var key = new Key();
        var randomness = RandomNumberGenerator.GetBytes(32);

        // Act
        var first = _service.GenerateNonce(randomness, key.PubKey, message: [1]);
        var second = _service.GenerateNonce(randomness, key.PubKey, message: [2]);
        var absent = _service.GenerateNonce(randomness, key.PubKey);
        var empty = _service.GenerateNonce(randomness, key.PubKey, message: []);

        // Assert
        Assert.NotEqual(first.PublicNonce, second.PublicNonce);
        Assert.NotEqual(absent.PublicNonce, empty.PublicNonce);
    }

    /// <summary>
    /// Two parties with random keys, the BIP 86 funding aggregate of their keys and a fresh nonce each for a random
    /// 32-byte message.
    /// </summary>
    private sealed record TwoPartySession(PrivKey KeyA, CompactPubKey PubKeyA, MusigNoncePair NonceA,
                                          PrivKey KeyB, CompactPubKey PubKeyB, MusigNoncePair NonceB,
                                          MusigKeyAggregate Aggregate, byte[] Message, MusigSigningSession Session)
    {
        public static TwoPartySession Create(Musig2Service service, byte[] message)
        {
            var keyA = new Key();
            var keyB = new Key();
            var pubKeyA = keyA.PubKey;
            var pubKeyB = keyB.PubKey;
            var aggregate = service.AggregateTaprootKeyPath(pubKeyA, pubKeyB);
            var nonceA = service.GenerateNonce(RandomNumberGenerator.GetBytes(32), pubKeyA, keyA.ToBytes(),
                                               aggregate.XOnlyOutputKey);
            var nonceB = service.GenerateNonce(RandomNumberGenerator.GetBytes(32), pubKeyB);
            var session = aggregate.CreateSession(service.AggregateNonces([nonceA.PublicNonce, nonceB.PublicNonce]),
                                                  message);
            return new TwoPartySession(keyA.ToBytes(), pubKeyA, nonceA, keyB.ToBytes(), pubKeyB, nonceB, aggregate,
                                       message, session);
        }
    }

    private sealed class Key
    {
        private readonly ECPrivKey _key = ECPrivKey.Create(RandomNumberGenerator.GetBytes(32));

        public CompactPubKey PubKey => _key.CreatePubKey().ToBytes(true);

        public byte[] ToBytes()
        {
            var bytes = new byte[32];
            _key.WriteToSpan(bytes);
            return bytes;
        }
    }
}