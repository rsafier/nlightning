using System.Security.Cryptography;

namespace NLightning.Infrastructure.Crypto.Ciphers;

using Domain.Crypto.Constants;
using Factories;
using Interfaces;

/// <summary>
/// Raw ChaCha20 keystream from <see href="https://www.rfc-editor.org/rfc/rfc8439">RFC 8439</see> (IETF variant) with a
/// fixed 96-bit all-zero nonce and block counter 0, as used by the BOLT 4 onion construction (pseudo-random stream
/// generation from the <c>rho</c>, <c>pad</c>, <c>um</c> and <c>ammag</c> keys).
/// </summary>
/// <remarks>
/// This is not an AEAD: nothing is authenticated. Because the nonce is fixed, a key always yields the same keystream,
/// so a key must never be reused to encrypt two different messages.
/// </remarks>
public sealed class ChaCha20Stream : IDisposable
{
    private const int KeyLen = CryptoConstants.PrivkeyLen;
    private const int NonceLen = CryptoConstants.Chacha20Poly1305NonceLen;

    private readonly ICryptoProvider _cryptoProvider;

    public ChaCha20Stream()
    {
        _cryptoProvider = CryptoFactory.GetCryptoProvider();
    }

    /// <summary>
    /// Fills <paramref name="output"/> with the ChaCha20 keystream for <paramref name="key"/> (zero nonce, counter 0).
    /// </summary>
    /// <param name="key">A 32-byte key.</param>
    /// <param name="output">The buffer to fill; its length is the number of keystream bytes produced.</param>
    /// <exception cref="ArgumentException">Thrown when the key is not 32 bytes.</exception>
    /// <exception cref="CryptographicException">Thrown when the provider fails.</exception>
    public void GenerateStream(ReadOnlySpan<byte> key, Span<byte> output)
    {
        output.Clear();
        Xor(key, output, output);
    }

    /// <summary>
    /// XORs <paramref name="input"/> with the ChaCha20 keystream for <paramref name="key"/> (zero nonce, counter 0)
    /// and writes the result into <paramref name="output"/>.
    /// </summary>
    /// <param name="key">A 32-byte key.</param>
    /// <param name="input">The data to XOR with the keystream.</param>
    /// <param name="output">
    /// A buffer of the same length as <paramref name="input"/>. It may be the same memory as the input (in-place), but
    /// must not partially overlap it.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when the key is not 32 bytes, the output length differs from the input length, or the buffers partially
    /// overlap.
    /// </exception>
    /// <exception cref="CryptographicException">Thrown when the provider fails.</exception>
    public void Xor(ReadOnlySpan<byte> key, ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (key.Length != KeyLen)
            throw new ArgumentException($"Key must be {KeyLen} bytes.", nameof(key));

        if (output.Length != input.Length)
            throw new ArgumentException("Output must be the same length as input.", nameof(output));

        if (input.Overlaps(output, out var elementOffset) && elementOffset != 0)
            throw new ArgumentException("Input and output must not partially overlap.", nameof(output));

        if (input.IsEmpty)
            return;

        Span<byte> nonce = stackalloc byte[NonceLen];
        nonce.Clear();

        var result = _cryptoProvider.StreamChaCha20IetfXor(key, nonce, input, output);
        if (result != 0)
            throw new CryptographicException("ChaCha20 stream failed.");
    }

    public void Dispose()
    {
        _cryptoProvider.Dispose();
    }
}