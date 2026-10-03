namespace NLightning.Domain.Tests.Crypto.ValueObjects;

using Domain.Crypto.Constants;
using Domain.Crypto.Models;
using Domain.Crypto.ValueObjects;

/// <summary>
/// The BIP 327 (MuSig2) value objects: lengths, conversions, the <c>cond ? null : value</c> gotcha, the 98-byte
/// <c>PartialSignatureWithNonce</c>, and the secret nonce's single use, clearing and redaction.
/// </summary>
public class MusigValueObjectTests
{
    private static readonly byte[] s_pubKey =
        Convert.FromHexString("03b7203dec7c13896b6ff1f58b24f84458c441720a12b5a57426397e22f0a8c78b");

    private static byte[] Bytes(int length, byte seed) => Enumerable.Range(0, length).Select(i => (byte)(seed + i))
                                                                    .ToArray();

    private static byte[] SecretNonceBytes()
    {
        var bytes = new byte[MusigConstants.SecretNonceLen];
        Bytes(MusigConstants.SecretNonceScalarsLen, 0x11).CopyTo(bytes, 0);
        s_pubKey.CopyTo(bytes, MusigConstants.SecretNonceScalarsLen);
        return bytes;
    }

    [Theory]
    [InlineData(65)]
    [InlineData(67)]
    public void Given_AWrongLength_When_BuildingNonces_Then_Refused(int length)
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => new MusigPublicNonce(new byte[length]));
        Assert.Throws<ArgumentException>(() => new MusigAggregateNonce(new byte[length]));
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    public void Given_AWrongLength_When_BuildingAPartialSignatureOrTweak_Then_Refused(int length)
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => new MusigPartialSignature(new byte[length]));
        Assert.Throws<ArgumentException>(() => new MusigTweak(new byte[length], true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Given_ConditionalWithBareNull_When_Evaluated_Then_YieldsNullableValuesWithoutThrowing(bool isNull)
    {
        // Arrange
        var nonce = Bytes(MusigConstants.PublicNonceLen, 1);
        var signature = Bytes(MusigConstants.PartialSignatureLen, 1);

        // Act
        MusigPublicNonce? publicNonce = isNull ? null : new MusigPublicNonce(nonce);
        MusigAggregateNonce? aggregateNonce = isNull ? null : new MusigAggregateNonce(nonce);
        MusigPartialSignature? partialSignature = isNull ? null : new MusigPartialSignature(signature);

        // Assert
        Assert.Equal(isNull, publicNonce is null);
        Assert.Equal(isNull, aggregateNonce is null);
        Assert.Equal(isNull, partialSignature is null);
    }

    [Fact]
    public void Given_ByteArrays_When_ImplicitlyConverted_Then_ValuesAreCopiesWithEqualContent()
    {
        // Arrange
        var nonce = Bytes(MusigConstants.PublicNonceLen, 7);
        var signature = Bytes(MusigConstants.PartialSignatureLen, 7);

        // Act
        MusigPublicNonce publicNonce = nonce;
        MusigPartialSignature partialSignature = signature;
        nonce[0] ^= 0xFF;

        // Assert
        Assert.NotEqual(nonce, (byte[])publicNonce);
        Assert.Equal(new MusigPublicNonce(Bytes(MusigConstants.PublicNonceLen, 7)), publicNonce);
        Assert.Equal(signature, (byte[])partialSignature);
        Assert.Equal(Convert.ToHexStringLower(signature), partialSignature.ToString());
    }

    [Fact]
    public void Given_ASignatureAndANonce_When_CombinedAndReadBack_Then_The98BytesAreSigThenNonce()
    {
        // Arrange
        MusigPartialSignature signature = Bytes(MusigConstants.PartialSignatureLen, 0x20);
        MusigPublicNonce nonce = Bytes(MusigConstants.PublicNonceLen, 0x80);

        // Act
        var combined = new MusigPartialSignatureWithNonce(signature, nonce);
        byte[] bytes = combined;
        MusigPartialSignatureWithNonce readBack = bytes;

        // Assert
        Assert.Equal(MusigConstants.PartialSignatureWithNonceLen, bytes.Length);
        Assert.Equal((byte[])signature, bytes[..32]);
        Assert.Equal((byte[])nonce, bytes[32..]);
        Assert.Equal(combined, readBack);
        Assert.Throws<ArgumentException>(() => new MusigPartialSignatureWithNonce(new byte[97]));
    }

    [Fact]
    public void Given_ASecretNonce_When_Consumed_Then_ItCopiesOnceAndRefusesAgain()
    {
        // Arrange
        var bytes = SecretNonceBytes();
        var secretNonce = new MusigSecretNonce(bytes);
        var first = new byte[MusigConstants.SecretNonceLen];
        var second = new byte[MusigConstants.SecretNonceLen];

        // Act
        secretNonce.Consume(first);
        var exception = Assert.Throws<InvalidOperationException>(() => secretNonce.Consume(second));

        // Assert
        Assert.Equal(bytes, first);
        Assert.True(secretNonce.IsUsed);
        Assert.All(second, b => Assert.Equal(0, b));
        Assert.Contains("already used", exception.Message);
        Assert.Equal(s_pubKey, (byte[])secretNonce.PublicKey);
    }

    [Fact]
    public void Given_ASecretNonce_When_Cleared_Then_ItCannotBeConsumed()
    {
        // Arrange
        var secretNonce = new MusigSecretNonce(SecretNonceBytes());

        // Act
        secretNonce.Dispose();

        // Assert
        Assert.True(secretNonce.IsUsed);
        Assert.Throws<InvalidOperationException>(() => secretNonce.Consume(new byte[MusigConstants.SecretNonceLen]));
    }

    [Fact]
    public void Given_ASecretNonce_When_Built_Then_ItCopiesItsInputAndNeverShowsTheSecret()
    {
        // Arrange
        var bytes = SecretNonceBytes();
        var secretNonce = new MusigSecretNonce(bytes);
        var copy = new byte[MusigConstants.SecretNonceLen];

        // Act
        Array.Clear(bytes);
        secretNonce.Consume(copy);
        var text = secretNonce.ToString();

        // Assert
        Assert.Equal(SecretNonceBytes(), copy);
        Assert.DoesNotContain(Convert.ToHexStringLower(copy.AsSpan(0, 32)), text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("redacted", text);
    }

    [Theory]
    [InlineData(96)]
    [InlineData(98)]
    public void Given_AWrongLength_When_BuildingASecretNonce_Then_Refused(int length)
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => new MusigSecretNonce(new byte[length]));
    }

    [Fact]
    public void Given_ANoncePair_When_Printed_Then_TheSecretNonceStaysRedacted()
    {
        // Arrange
        var pair = new MusigNoncePair(new MusigSecretNonce(SecretNonceBytes()),
                                      Bytes(MusigConstants.PublicNonceLen, 3));

        // Act
        var text = pair.ToString();

        // Assert
        Assert.DoesNotContain(Convert.ToHexStringLower(Bytes(MusigConstants.SecretNonceScalarsLen, 0x11)), text,
                              StringComparison.OrdinalIgnoreCase);
        Assert.Contains("redacted", text);
    }

    [Fact]
    public void Given_AKeyAggregate_When_AskedForTheTaprootScript_Then_ItIsOp1PushOfTheXOnlyOutputKey()
    {
        // Arrange
        CompactPubKey outputKey = s_pubKey;
        var aggregate = new MusigKeyAggregate([outputKey], [], outputKey, outputKey);

        byte[] expected = [0x51, 0x20, .. s_pubKey[1..]];

        // Act
        var script = aggregate.GetTaprootScriptPubKey();

        // Assert
        Assert.Equal(expected, script);
        Assert.Equal(s_pubKey[1..], aggregate.XOnlyOutputKey);
        Assert.True(aggregate.OutputKeyHasOddY);
    }

    [Fact]
    public void Given_TwoTweaks_When_Compared_Then_EqualByContentAndKind()
    {
        // Arrange
        var value = Bytes(MusigConstants.TweakLen, 9);

        // Act
        var xOnly = new MusigTweak(value, true);
        var sameXOnly = new MusigTweak(Bytes(MusigConstants.TweakLen, 9), true);
        var plain = new MusigTweak(value, false);

        // Assert
        Assert.Equal(xOnly, sameXOnly);
        Assert.Equal(xOnly.GetHashCode(), sameXOnly.GetHashCode());
        Assert.NotEqual(xOnly, plain);
    }
}