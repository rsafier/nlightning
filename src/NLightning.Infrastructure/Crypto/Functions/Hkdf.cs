using System.Diagnostics;

namespace NLightning.Infrastructure.Crypto.Functions;

using Domain.Crypto.Constants;
using Primitives;

/// <summary>
/// HMAC-based Extract-and-Expand Key Derivation Function, defined in
/// <see href="https://tools.ietf.org/html/rfc5869">RFC 5869</see>.
/// </summary>
internal sealed class Hkdf : IDisposable
{
    private static readonly byte[] s_one = [1];
    private static readonly byte[] s_two = [2];
    private static readonly byte[] s_three = [3];

    private readonly HmacSha256 _hmacSha256 = new();

    private bool _disposed;

    /// <summary>
    /// Takes a chainingKey byte sequence of length HashLen,
    /// and an inputKeyMaterial byte sequence with length
    /// either zero bytes, 32 bytes, or DhLen bytes. Writes a
    /// byte sequences of length 2 * HashLen into output parameter.
    /// </summary>
    public void ExtractAndExpand2(SecureMemory chainingKey, ReadOnlySpan<byte> inputKeyMaterial, Span<byte> output)
    {
        // ExceptionUtils.ThrowIfDisposed(_disposed, nameof(Hkdf));

        Debug.Assert(chainingKey.Length == CryptoConstants.Sha256HashLen);
        Debug.Assert(output.Length == 2 * CryptoConstants.Sha256HashLen);

        Span<byte> tempKey = stackalloc byte[CryptoConstants.Sha256HashLen];
        HmacHash(chainingKey, tempKey, inputKeyMaterial);

        var output1 = output[..CryptoConstants.Sha256HashLen];
        HmacHash(tempKey, output1, s_one);

        var output2 = output.Slice(CryptoConstants.Sha256HashLen, CryptoConstants.Sha256HashLen);
        HmacHash(tempKey, output2, output1, s_two);
    }

    /// <summary>
    /// Takes a chainingKey byte sequence of length HashLen,
    /// and an inputKeyMaterial byte sequence with length
    /// either zero bytes, 32 bytes, or DhLen bytes. Writes a
    /// byte sequences of length 3 * HashLen into output parameter.
    /// </summary>
    public void ExtractAndExpand3(SecureMemory chainingKey, ReadOnlySpan<byte> inputKeyMaterial, Span<byte> output)
    {
        // ExceptionUtils.ThrowIfDisposed(_disposed, nameof(Hkdf));

        Debug.Assert(chainingKey.Length == CryptoConstants.Sha256HashLen);
        Debug.Assert(output.Length == 3 * CryptoConstants.Sha256HashLen);

        Span<byte> tempKey = stackalloc byte[CryptoConstants.Sha256HashLen];
        HmacHash(chainingKey, tempKey, inputKeyMaterial);

        var output1 = output[..CryptoConstants.Sha256HashLen];
        HmacHash(tempKey, output1, s_one);

        var output2 = output.Slice(CryptoConstants.Sha256HashLen, CryptoConstants.Sha256HashLen);
        HmacHash(tempKey, output2, output1, s_two);

        var output3 = output.Slice(2 * CryptoConstants.Sha256HashLen, CryptoConstants.Sha256HashLen);
        HmacHash(tempKey, output3, output2, s_three);
    }

    private void HmacHash(ReadOnlySpan<byte> key, Span<byte> hmac, ReadOnlySpan<byte> data1 = default, ReadOnlySpan<byte> data2 = default)
    {
        // ExceptionUtils.ThrowIfDisposed(_disposed, nameof(Hkdf));

        Debug.Assert(key.Length == CryptoConstants.Sha256HashLen);
        Debug.Assert(hmac.Length == CryptoConstants.Sha256HashLen);

        _hmacSha256.ComputeHash(key, data1, data2, hmac);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _hmacSha256.Dispose();

        _disposed = true;
    }
}