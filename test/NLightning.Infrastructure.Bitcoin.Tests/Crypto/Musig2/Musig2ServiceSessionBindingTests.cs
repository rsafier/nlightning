using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Crypto.Musig2;

using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Infrastructure.Bitcoin.Crypto.Musig2;

/// <summary>
/// NL-904 item 3: the just-in-time <c>GenerateNonce</c> overload (fresh randomness, secret key required) and sessions
/// bound to the public nonces their aggregate came from.
/// </summary>
public class Musig2ServiceSessionBindingTests
{
    private readonly Musig2Service _service = new();
    private readonly Key _keyA = new(Enumerable.Repeat((byte)0x11, 32).ToArray());
    private readonly Key _keyB = new(Enumerable.Repeat((byte)0x22, 32).ToArray());
    private readonly byte[] _message = Enumerable.Repeat((byte)0x5a, 32).ToArray();

    [Fact]
    public void Given_TheSameInputs_When_GeneratingJitNonces_Then_EachIsFreshAndSigns()
    {
        // Arrange
        var aggregate = _service.AggregateTaprootKeyPath(_keyA.PubKey.ToBytes(), _keyB.PubKey.ToBytes());

        // Act
        var first = _service.GenerateNonce(_keyA.PubKey.ToBytes(), new PrivKey(_keyA.ToBytes()),
                                           aggregate.XOnlyOutputKey, _message);
        var second = _service.GenerateNonce(_keyA.PubKey.ToBytes(), new PrivKey(_keyA.ToBytes()),
                                            aggregate.XOnlyOutputKey, _message);
        var other = _service.GenerateNonce(_keyB.PubKey.ToBytes(), new PrivKey(_keyB.ToBytes()));
        var session = _service.CreateSession(aggregate, [first.PublicNonce, other.PublicNonce], _message);
        var signature = _service.Sign(first.SecretNonce, _keyA.ToBytes(), session);

        // Assert
        Assert.NotEqual(first.PublicNonce, second.PublicNonce);
        Assert.True(_service.VerifyPartialSignature(signature, first.PublicNonce, _keyA.PubKey.ToBytes(), session));
        second.SecretNonce.Dispose();
        other.SecretNonce.Dispose();
    }

    [Fact]
    public void Given_NoSecretKey_When_GeneratingAJitNonce_Then_ItThrows()
    {
        // Act / Assert: the overload requires the key (default PrivKey has none)
        Assert.Throws<ArgumentNullException>(() => _service.GenerateNonce(_keyA.PubKey.ToBytes(), default(PrivKey)));
    }

    [Fact]
    public void Given_ASessionFromItsNonces_When_VerifyingWithAnotherNonceOrASwappedAggregate_Then_ItIsFalse()
    {
        // Arrange
        var aggregate = _service.AggregateTaprootKeyPath(_keyA.PubKey.ToBytes(), _keyB.PubKey.ToBytes());
        var nonceA = _service.GenerateNonce(_keyA.PubKey.ToBytes(), new PrivKey(_keyA.ToBytes()));
        var nonceB = _service.GenerateNonce(_keyB.PubKey.ToBytes(), new PrivKey(_keyB.ToBytes()));
        var stranger = _service.GenerateNonce(_keyB.PubKey.ToBytes(), new PrivKey(_keyB.ToBytes()));
        var session = _service.CreateSession(aggregate, [nonceA.PublicNonce, nonceB.PublicNonce], _message);
        var signature = _service.Sign(nonceA.SecretNonce, _keyA.ToBytes(), session);

        // Act: a nonce outside the session, and the same session with another aggregate swapped in
        var swapped = session with
        {
            AggregateNonce = _service.AggregateNonces([nonceA.PublicNonce, stranger.PublicNonce])
        };

        // Assert
        Assert.True(_service.VerifyPartialSignature(signature, nonceA.PublicNonce, _keyA.PubKey.ToBytes(), session));
        Assert.False(_service.VerifyPartialSignature(signature, stranger.PublicNonce, _keyA.PubKey.ToBytes(),
                                                     session));
        Assert.False(_service.VerifyPartialSignature(signature, nonceA.PublicNonce, _keyA.PubKey.ToBytes(),
                                                     swapped));
        nonceB.SecretNonce.Dispose();
        stranger.SecretNonce.Dispose();
    }

    [Fact]
    public void Given_TheWrongNumberOfNonces_When_CreatingASession_Then_ItThrows()
    {
        // Arrange
        var aggregate = _service.AggregateTaprootKeyPath(_keyA.PubKey.ToBytes(), _keyB.PubKey.ToBytes());
        var nonceA = _service.GenerateNonce(_keyA.PubKey.ToBytes(), new PrivKey(_keyA.ToBytes()));

        // Act / Assert
        Assert.Throws<MusigException>(() => _service.CreateSession(aggregate, [nonceA.PublicNonce], _message));
        nonceA.SecretNonce.Dispose();
    }
}