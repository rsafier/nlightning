namespace NLightning.Infrastructure.Tests.Crypto.Ciphers;

using Infrastructure.Crypto.Ciphers;

/// <summary>
/// Tests for the raw ChaCha20 keystream (zero nonce, counter 0) against
/// <see href="https://www.rfc-editor.org/rfc/rfc8439">RFC 8439</see> vectors.
/// Runs against whichever crypto provider the build configuration selects (libsodium or native).
/// </summary>
public class ChaCha20StreamTests
{
    // RFC 8439 A.1 test vector #1: key = 0^32, nonce = 0^12, block counter = 0
    private static readonly byte[] s_rfc8439A1Vector1 = Convert.FromHexString(
        "76b8e0ada0f13d90405d6ae55386bd28bdd219b8a08ded1aa836efcc8b770dc7" +
        "da41597c5157488d7724e03fb8d84a376a43b8f41518a11cc387b669b2ee6586");

    // RFC 8439 A.1 test vector #2: key = 0^32, nonce = 0^12, block counter = 1
    private static readonly byte[] s_rfc8439A1Vector2 = Convert.FromHexString(
        "9f07e7be5551387a98ba977c732d080dcb0f29a048e3656912c6533e32ee7aed" +
        "29b721769ce64e43d57133b074d839d531ed1f28510afb45ace10a1f4b794d6f");

    // RFC 8439 2.4.2 key and plaintext
    private static readonly byte[] s_rfc8439Key = Convert.FromHexString(
        "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");

    private static readonly byte[] s_rfc8439Plaintext =
        "Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it."u8
           .ToArray();

    // RFC 8439 2.4.2 plaintext and key, but with the zero nonce and block counter 0 used by BOLT 4
    // (cross-checked with an independent ChaCha20 implementation: Python cryptography/OpenSSL)
    private static readonly byte[] s_rfc8439PlaintextZeroNonceCounter0Ciphertext = Convert.FromHexString(
        "759c4f14bcb6390be3d92330ddb23e25ef58bd019cb10cecc6a4186cbb8645e1" +
        "5803a38182471a92052ea60f1a815f074af7598e9198c54ba75542d9272e6163" +
        "76d43b11c288c3f167082c41c92c3c078cd996d587d82f299e9dd03259401b2f" +
        "117a6d08bfce4b32e8ed3c04066b2055ef8e");

    [Fact]
    public void Given_ZeroKey_When_GenerateStream_Then_MatchesRfc8439A1Vector1()
    {
        // Arrange
        using var chaCha20 = new ChaCha20Stream();
        var key = new byte[32];
        var output = new byte[64];

        // Act
        chaCha20.GenerateStream(key, output);

        // Assert
        Assert.Equal(s_rfc8439A1Vector1, output);
    }

    [Fact]
    public void Given_ZeroKey_When_GenerateStreamOfTwoBlocks_Then_SecondBlockMatchesRfc8439A1Vector2()
    {
        // Arrange
        using var chaCha20 = new ChaCha20Stream();
        var key = new byte[32];
        var output = new byte[128];

        // Act
        chaCha20.GenerateStream(key, output);

        // Assert
        Assert.Equal(s_rfc8439A1Vector1, output[..64]);
        Assert.Equal(s_rfc8439A1Vector2, output[64..]);
    }

    [Fact]
    public void Given_DirtyOutputBuffer_When_GenerateStream_Then_OutputIsPureKeystream()
    {
        // Arrange
        using var chaCha20 = new ChaCha20Stream();
        var key = new byte[32];
        var output = Enumerable.Repeat((byte)0xAA, 64).ToArray();

        // Act
        chaCha20.GenerateStream(key, output);

        // Assert
        Assert.Equal(s_rfc8439A1Vector1, output);
    }

    [Fact]
    public void Given_Rfc8439Plaintext_When_Xor_Then_MatchesKnownCiphertext()
    {
        // Arrange
        using var chaCha20 = new ChaCha20Stream();
        var output = new byte[s_rfc8439Plaintext.Length];

        // Act
        chaCha20.Xor(s_rfc8439Key, s_rfc8439Plaintext, output);

        // Assert
        Assert.Equal(s_rfc8439PlaintextZeroNonceCounter0Ciphertext, output);
    }

    [Fact]
    public void Given_Ciphertext_When_XorInPlace_Then_PlaintextIsRecovered()
    {
        // Arrange
        using var chaCha20 = new ChaCha20Stream();
        var buffer = s_rfc8439PlaintextZeroNonceCounter0Ciphertext.ToArray();

        // Act
        chaCha20.Xor(s_rfc8439Key, buffer, buffer);

        // Assert
        Assert.Equal(s_rfc8439Plaintext, buffer);
    }

    [Fact]
    public void Given_SameKey_When_GenerateStreamAndAeadEncryptZeros_Then_StreamStartsOneBlockBeforeAead()
    {
        // Arrange
        using var chaCha20 = new ChaCha20Stream();
        using var aead = new ChaCha20Poly1305();
        var key = new byte[32];
        var stream = new byte[128];
        var aeadCiphertext = new byte[64 + 16];

        // Act
        chaCha20.GenerateStream(key, stream);
        // nonce 0 => 12 zero bytes; AEAD encrypts with block counter 1 (counter 0 is the Poly1305 key)
        aead.Encrypt(key, 0, ReadOnlySpan<byte>.Empty, new byte[64], aeadCiphertext);

        // Assert
        Assert.NotEqual(aeadCiphertext[..64], stream[..64]);
        Assert.Equal(aeadCiphertext[..64], stream[64..]);
    }

    [Fact]
    public void Given_EmptyInput_When_Xor_Then_DoesNothing()
    {
        // Arrange
        using var chaCha20 = new ChaCha20Stream();

        // Act
        var exception = Record.Exception(() => chaCha20.Xor(new byte[32], ReadOnlySpan<byte>.Empty, Span<byte>.Empty));

        // Assert
        Assert.Null(exception);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    public void Given_InvalidKeyLength_When_GenerateStream_Then_ThrowsArgumentException(int keyLength)
    {
        // Arrange
        using var chaCha20 = new ChaCha20Stream();
        var key = new byte[keyLength];
        var output = new byte[64];

        // Act & Assert
        Assert.Throws<ArgumentException>(() => chaCha20.GenerateStream(key, output));
    }

    [Fact]
    public void Given_OutputLengthDifferentFromInput_When_Xor_Then_ThrowsArgumentException()
    {
        // Arrange
        using var chaCha20 = new ChaCha20Stream();
        var input = new byte[64];
        var output = new byte[63];

        // Act & Assert
        Assert.Throws<ArgumentException>(() => chaCha20.Xor(new byte[32], input, output));
    }

    [Fact]
    public void Given_PartiallyOverlappingBuffers_When_Xor_Then_ThrowsArgumentException()
    {
        // Arrange
        using var chaCha20 = new ChaCha20Stream();
        var buffer = new byte[65];

        // Act & Assert
        Assert.Throws<ArgumentException>(() => chaCha20.Xor(new byte[32], buffer.AsSpan(0, 64),
                                                            buffer.AsSpan(1, 64)));
    }
}