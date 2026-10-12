using System.Security.Cryptography;

namespace NLightning.Infrastructure.Crypto.Functions;

using Domain.Crypto.Constants;
using Hashes;

/// <summary>
/// HMAC-SHA256 as defined in <see href="https://tools.ietf.org/html/rfc2104">RFC 2104</see>,
/// accepting keys of any length (keys longer than the SHA-256 block size are hashed first).
/// </summary>
/// <remarks>
/// Built on top of <see cref="Sha256"/> so it works with every crypto backend.
/// An instance reuses its underlying hash state and is not thread-safe.
/// </remarks>
public sealed class HmacSha256 : IDisposable
{
    private const byte InnerPad = 0x36;
    private const byte OuterPad = 0x5C;

    private readonly Sha256 _sha256 = new();

    private bool _disposed;

    /// <summary>
    /// Computes HMAC-SHA256(key, data) and writes the 32-byte result into <paramref name="output"/>.
    /// </summary>
    /// <param name="key">The key, of any length.</param>
    /// <param name="data">The message to authenticate.</param>
    /// <param name="output">Destination of exactly <see cref="CryptoConstants.Sha256HashLen"/> bytes.</param>
    /// <exception cref="ArgumentException">Thrown if <paramref name="output"/> is not 32 bytes long.</exception>
    public void ComputeHash(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data, Span<byte> output)
    {
        ComputeHash(key, data, ReadOnlySpan<byte>.Empty, output);
    }

    /// <summary>
    /// Computes HMAC-SHA256(key, data1 || data2) and writes the 32-byte result into <paramref name="output"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="output"/> may overlap <paramref name="key"/>, <paramref name="data1"/> or
    /// <paramref name="data2"/>: it is only written after all inputs have been consumed.
    /// </remarks>
    /// <param name="key">The key, of any length.</param>
    /// <param name="data1">The first part of the message to authenticate.</param>
    /// <param name="data2">The second part of the message to authenticate.</param>
    /// <param name="output">Destination of exactly <see cref="CryptoConstants.Sha256HashLen"/> bytes.</param>
    /// <exception cref="ArgumentException">Thrown if <paramref name="output"/> is not 32 bytes long.</exception>
    public void ComputeHash(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data1, ReadOnlySpan<byte> data2,
                            Span<byte> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (output.Length != CryptoConstants.Sha256HashLen)
        {
            throw new ArgumentException($"Output must be {CryptoConstants.Sha256HashLen} bytes long.",
                                        nameof(output));
        }

        Span<byte> ipad = stackalloc byte[CryptoConstants.Sha256BlockLen];
        Span<byte> opad = stackalloc byte[CryptoConstants.Sha256BlockLen];
        Span<byte> innerHash = stackalloc byte[CryptoConstants.Sha256HashLen];

        try
        {
            // K0: keys longer than the block size are hashed first, shorter keys are zero-padded
            ipad.Clear();
            if (key.Length > CryptoConstants.Sha256BlockLen)
            {
                _sha256.AppendData(key);
                _sha256.GetHashAndReset(ipad[..CryptoConstants.Sha256HashLen]);
            }
            else
            {
                key.CopyTo(ipad);
            }

            ipad.CopyTo(opad);

            for (var i = 0; i < CryptoConstants.Sha256BlockLen; ++i)
            {
                ipad[i] ^= InnerPad;
                opad[i] ^= OuterPad;
            }

            _sha256.AppendData(ipad);
            _sha256.AppendData(data1);
            _sha256.AppendData(data2);
            _sha256.GetHashAndReset(innerHash);

            _sha256.AppendData(opad);
            _sha256.AppendData(innerHash);
            _sha256.GetHashAndReset(output);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ipad);
            CryptographicOperations.ZeroMemory(opad);
            CryptographicOperations.ZeroMemory(innerHash);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _sha256.Dispose();

        _disposed = true;
    }
}