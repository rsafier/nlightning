using System.Security.Cryptography;
using System.Text;

namespace NLightning.Infrastructure.Node.PeerStorage;

using Crypto.Ciphers;
using Crypto.Functions;
using Domain.Crypto.Constants;
using Domain.Node.PeerStorage;
using Domain.Protocol.Interfaces;

/// <summary>
/// XChaCha20-Poly1305 over our peer storage blobs. The key is <c>HMAC-SHA256(node_secret, "nltg peer storage v1")</c>,
/// so only this node (or its restored seed) can read or forge a blob. Layout: version (1 byte, 1), a random 24-byte
/// nonce, then the ciphertext and its 16-byte tag; the version byte is authenticated as associated data.
/// </summary>
public sealed class PeerStorageCipher : IPeerStorageCipher
{
    internal const byte Version = 1;
    private const int NonceLength = 24;
    private const int HeaderLength = 1 + NonceLength;

    private static readonly byte[] s_keyLabel = Encoding.ASCII.GetBytes("nltg peer storage v1");

    private readonly ISecureKeyManager _secureKeyManager;

    public PeerStorageCipher(ISecureKeyManager secureKeyManager)
    {
        _secureKeyManager = secureKeyManager;
    }

    /// <inheritdoc />
    public int MaxPlaintextLength =>
        PeerStorageConstants.MaxBlobLength - HeaderLength - CryptoConstants.Xchacha20Poly1305TagLen;

    /// <inheritdoc />
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.Length > MaxPlaintextLength)
            throw new ArgumentException($"The plaintext is at most {MaxPlaintextLength} bytes, got {plaintext.Length}",
                                        nameof(plaintext));

        var blob = new byte[HeaderLength + plaintext.Length + CryptoConstants.Xchacha20Poly1305TagLen];
        blob[0] = Version;
        RandomNumberGenerator.Fill(blob.AsSpan(1, NonceLength));

        Span<byte> key = stackalloc byte[CryptoConstants.PrivkeyLen];
        try
        {
            DeriveKey(key);
            using var cipher = new XChaCha20Poly1305();
            cipher.Encrypt(key, blob.AsSpan(1, NonceLength), blob.AsSpan(0, 1), plaintext,
                           blob.AsSpan(HeaderLength));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return blob;
    }

    /// <inheritdoc />
    public byte[]? TryDecrypt(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < HeaderLength + CryptoConstants.Xchacha20Poly1305TagLen || blob[0] != Version)
            return null;

        var ciphertext = blob[HeaderLength..];
        var plaintext = new byte[ciphertext.Length - CryptoConstants.Xchacha20Poly1305TagLen];
        Span<byte> key = stackalloc byte[CryptoConstants.PrivkeyLen];
        try
        {
            DeriveKey(key);
            using var cipher = new XChaCha20Poly1305();
            cipher.Decrypt(key, blob.Slice(1, NonceLength), blob[..1], ciphertext, plaintext);
            return plaintext;
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private void DeriveKey(Span<byte> key)
    {
        var nodeKey = _secureKeyManager.GetNodeKeyPair();
        byte[] secret = nodeKey.PrivKey;
        try
        {
            using var hmac = new HmacSha256();
            hmac.ComputeHash(secret, s_keyLabel, key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }
}