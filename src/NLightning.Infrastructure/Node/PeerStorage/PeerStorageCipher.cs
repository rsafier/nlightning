using System.Security.Cryptography;

namespace NLightning.Infrastructure.Node.PeerStorage;

using Domain.Crypto.Constants;
using Domain.Node.PeerStorage;
using Domain.Protocol.Enums;
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

        var ciphertext = _secureKeyManager.EncryptNodeData(NodeDataPurpose.PeerStorage,
                                                           blob.AsSpan(1, NonceLength).ToArray(), [Version],
                                                           plaintext.ToArray());
        ciphertext.CopyTo(blob, HeaderLength);

        return blob;
    }

    /// <inheritdoc />
    public byte[]? TryDecrypt(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < HeaderLength + CryptoConstants.Xchacha20Poly1305TagLen || blob[0] != Version)
            return null;

        try
        {
            return _secureKeyManager.DecryptNodeData(NodeDataPurpose.PeerStorage,
                                                     blob.Slice(1, NonceLength).ToArray(), [Version],
                                                     blob[HeaderLength..].ToArray());
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}