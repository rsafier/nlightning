using System.Security.Cryptography;
using NBitcoin.Secp256k1;

namespace NLightning.Infrastructure.Bitcoin.Onion;

using Crypto.Contexts;
using Domain.Crypto.Constants;
using Infrastructure.Crypto.Functions;
using Infrastructure.Crypto.Hashes;

/// <summary>
/// BOLT 4 Sphinx key derivation: per-hop keys (<c>rho</c>, <c>mu</c>, <c>um</c>, <c>pad</c>, <c>ammag</c>, ...) and
/// ephemeral-key blinding factors.
/// </summary>
/// <remarks>
/// An instance owns one <see cref="HmacSha256"/> and one <see cref="Sha256"/> so that a whole packet operation reuses
/// the same hash state. It is not thread-safe; create one per operation.
/// </remarks>
internal sealed class SphinxKeyGenerator : IDisposable
{
    private readonly HmacSha256 _hmacSha256 = new();
    private readonly Sha256 _sha256 = new();

    /// <summary>
    /// Derives a key as <c>HMAC-SHA256(key = label, msg = secret)</c>.
    /// </summary>
    /// <param name="label">The ASCII key type (e.g. <c>"rho"</c>), without a NUL terminator.</param>
    /// <param name="secret">The 32-byte shared secret (or session key for <c>pad</c>).</param>
    /// <param name="output">The 32-byte destination.</param>
    /// <exception cref="ArgumentException">If the label is empty or a buffer has the wrong length.</exception>
    public void DeriveKey(ReadOnlySpan<byte> label, ReadOnlySpan<byte> secret, Span<byte> output)
    {
        if (label.IsEmpty)
            throw new ArgumentException("Key label must not be empty.", nameof(label));

        EnsureLength(secret, CryptoConstants.SecretLen, nameof(secret));
        EnsureLength(output, CryptoConstants.Sha256HashLen, nameof(output));

        _hmacSha256.ComputeHash(label, secret, output);
    }

    /// <summary>
    /// Derives a key as <c>HMAC-SHA256(key = label, msg = secret)</c> into a new array.
    /// </summary>
    public byte[] DeriveKey(ReadOnlySpan<byte> label, ReadOnlySpan<byte> secret)
    {
        var key = new byte[CryptoConstants.Sha256HashLen];
        DeriveKey(label, secret, key);
        return key;
    }

    /// <summary>
    /// Computes the ephemeral-key blinding factor <c>SHA256(ephemeral_pubkey || shared_secret)</c>.
    /// </summary>
    /// <param name="ephemeralPubKey">The 33-byte compressed ephemeral public key.</param>
    /// <param name="sharedSecret">The 32-byte shared secret.</param>
    /// <param name="output">The 32-byte destination.</param>
    public void ComputeBlindingFactor(ReadOnlySpan<byte> ephemeralPubKey, ReadOnlySpan<byte> sharedSecret,
                                      Span<byte> output)
    {
        EnsureLength(ephemeralPubKey, CryptoConstants.CompactPubkeyLen, nameof(ephemeralPubKey));
        EnsureLength(sharedSecret, CryptoConstants.SecretLen, nameof(sharedSecret));
        EnsureLength(output, CryptoConstants.Sha256HashLen, nameof(output));

        _sha256.AppendData(ephemeralPubKey);
        _sha256.AppendData(sharedSecret);
        _sha256.GetHashAndReset(output);
    }

    /// <summary>
    /// Computes the ephemeral-key blinding factor <c>SHA256(ephemeral_pubkey || shared_secret)</c> into a new array.
    /// </summary>
    public byte[] ComputeBlindingFactor(ReadOnlySpan<byte> ephemeralPubKey, ReadOnlySpan<byte> sharedSecret)
    {
        var blindingFactor = new byte[CryptoConstants.Sha256HashLen];
        ComputeBlindingFactor(ephemeralPubKey, sharedSecret, blindingFactor);
        return blindingFactor;
    }

    /// <summary>
    /// Computes <c>HMAC-SHA256(key, data1 || data2)</c>, e.g. the packet HMAC over hop_payloads || associated_data.
    /// </summary>
    public void ComputeHmac(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data1, ReadOnlySpan<byte> data2,
                            Span<byte> output)
    {
        EnsureLength(output, CryptoConstants.Sha256HashLen, nameof(output));

        _hmacSha256.ComputeHash(key, data1, data2, output);
    }

    /// <summary>
    /// Computes <c>SHA256(data)</c>, e.g. sha256_of_onion for BADONION failures.
    /// </summary>
    public byte[] ComputeSha256(ReadOnlySpan<byte> data)
    {
        var hash = new byte[CryptoConstants.Sha256HashLen];
        _sha256.AppendData(data);
        _sha256.GetHashAndReset(hash);
        return hash;
    }

    /// <summary>
    /// Computes the Sphinx/BOLT 8 ECDH shared secret <c>SHA256(compressed(privateKey * publicKey))</c>.
    /// </summary>
    /// <remarks>
    /// Same result as <c>IEcdh.SecP256K1Dh</c>, but it reuses the parsed private key instead of allocating NBitcoin
    /// key wrappers per hop, and hashes the 33-byte point with the allocation-free one-shot BCL SHA-256.
    /// </remarks>
    /// <param name="privateKey">The private key.</param>
    /// <param name="publicKey">The 33-byte compressed public key.</param>
    /// <param name="output">The 32-byte destination.</param>
    /// <exception cref="ArgumentException">If the public key is invalid or <paramref name="output"/> is not 32 bytes.</exception>
    public void ComputeSharedSecret(ECPrivKey privateKey, ReadOnlySpan<byte> publicKey, Span<byte> output)
    {
        ComputeEcdhSharedSecret(privateKey, publicKey, output);
    }

    /// <inheritdoc cref="ComputeSharedSecret(ECPrivKey, ReadOnlySpan{byte}, Span{byte})"/>
    /// <remarks>Stateless (no generator needed), e.g. for the key manager's node-key ECDH.</remarks>
    public static void ComputeEcdhSharedSecret(ECPrivKey privateKey, ReadOnlySpan<byte> publicKey, Span<byte> output)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        EnsureLength(output, CryptoConstants.SecretLen, nameof(output));
        if (publicKey.Length != CryptoConstants.CompactPubkeyLen
         || !ECPubKey.TryCreate(publicKey, NLightningCryptoContext.Instance, out _, out var ecPubKey)
         || ecPubKey is null)
            throw new ArgumentException("Invalid public key.", nameof(publicKey));

        // The shared point is as secret as the shared secret derived from it
        Span<byte> sharedPoint = stackalloc byte[CryptoConstants.CompactPubkeyLen];
        try
        {
            ecPubKey.GetSharedPubkey(privateKey).WriteToSpan(true, sharedPoint, out _);
            System.Security.Cryptography.SHA256.HashData(sharedPoint, output);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedPoint);
        }
    }

    /// <inheritdoc cref="ComputeSharedSecret(ECPrivKey, ReadOnlySpan{byte}, Span{byte})"/>
    /// <exception cref="ArgumentException">If a key is invalid or <paramref name="output"/> is not 32 bytes.</exception>
    public void ComputeSharedSecret(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> publicKey, Span<byte> output)
    {
        using var ecPrivKey = CreatePrivateKey(privateKey, nameof(privateKey));
        ComputeSharedSecret(ecPrivKey, publicKey, output);
    }

    /// <summary>
    /// Parses a 32-byte private key in [1, n-1]; the caller owns (and disposes) the result.
    /// </summary>
    /// <exception cref="ArgumentException">If the key is invalid.</exception>
    public static ECPrivKey CreatePrivateKey(ReadOnlySpan<byte> privateKey, string paramName)
    {
        if (privateKey.Length != CryptoConstants.PrivkeyLen
         || !NLightningCryptoContext.Instance.TryCreateECPrivKey(privateKey, out var ecPrivKey)
         || ecPrivKey is null)
            throw new ArgumentException("Invalid private key.", paramName);

        return ecPrivKey;
    }

    /// <summary>
    /// Checks that <paramref name="publicKey"/> is a 33-byte compressed encoding of a point on secp256k1.
    /// </summary>
    public static bool IsValidPublicKey(ReadOnlySpan<byte> publicKey)
    {
        return publicKey.Length == CryptoConstants.CompactPubkeyLen
            && ECPubKey.TryCreate(publicKey, NLightningCryptoContext.Instance, out var compressed, out var ecPubKey)
            && compressed
            && ecPubKey is not null;
    }

    public void Dispose()
    {
        _hmacSha256.Dispose();
        _sha256.Dispose();
    }

    private static void EnsureLength(ReadOnlySpan<byte> buffer, int expectedLength, string paramName)
    {
        if (buffer.Length != expectedLength)
            throw new ArgumentException($"Expected {expectedLength} bytes, got {buffer.Length}.", paramName);
    }
}