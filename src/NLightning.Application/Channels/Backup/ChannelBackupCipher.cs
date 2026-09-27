using System.Security.Cryptography;
using System.Text;

namespace NLightning.Application.Channels.Backup;

using Domain.Crypto.Constants;
using Infrastructure.Crypto.Ciphers;

/// <summary>
/// Encrypts a static channel backup to the node's key. File layout:
/// <code>
/// 5 magic "NLSCB" | u8 file version (1) | 24 nonce | XChaCha20-Poly1305(plaintext) with the 16-byte tag
/// </code>
/// The key is HKDF-SHA256 (RFC 5869) of the node private key (salt <c>"NLightning static channel backup"</c>, info
/// <c>"nltg-scb-v1"</c>), so only a node with the same key file can read the backup; the header is the AEAD's
/// associated data. The nonce is random for every encryption (24 bytes: no practical collision), as LND does.
/// </summary>
public static class ChannelBackupCipher
{
    /// <summary>The file version this cipher writes and reads.</summary>
    public const byte FileVersion = 1;

    /// <summary>The length of the header (magic and version).</summary>
    public const int HeaderLength = 6;

    /// <summary>The fixed overhead of an encrypted backup over its plaintext.</summary>
    public const int Overhead = HeaderLength + NonceLength + CryptoConstants.Xchacha20Poly1305TagLen;

    private const int NonceLength = CryptoConstants.Xchacha20Poly1305NonceLen;
    private const int KeyLength = CryptoConstants.PrivkeyLen;

    private static readonly byte[] s_magic = "NLSCB"u8.ToArray();
    private static readonly byte[] s_salt = Encoding.ASCII.GetBytes("NLightning static channel backup");
    private static readonly byte[] s_info = Encoding.ASCII.GetBytes("nltg-scb-v1");

    /// <summary>Derives the backup key from the node private key.</summary>
    /// <param name="nodePrivateKey">The 32-byte node private key.</param>
    /// <returns>The 32-byte backup key; zero it after use.</returns>
    public static byte[] DeriveKey(ReadOnlySpan<byte> nodePrivateKey)
    {
        if (nodePrivateKey.Length != KeyLength)
            throw new ArgumentException($"The node private key must be {KeyLength} bytes.", nameof(nodePrivateKey));

        var key = new byte[KeyLength];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, nodePrivateKey, key, s_salt, s_info);
        return key;
    }

    /// <summary>Encrypts <paramref name="plaintext"/> with <paramref name="key"/> and a fresh random nonce.</summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext)
    {
        Span<byte> nonce = stackalloc byte[NonceLength];
        RandomNumberGenerator.Fill(nonce);
        return Encrypt(key, nonce, plaintext);
    }

    /// <summary>Encrypts <paramref name="plaintext"/> with <paramref name="key"/> and the given nonce (tests).</summary>
    internal static byte[] Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext)
    {
        if (key.Length != KeyLength)
            throw new ArgumentException($"The backup key must be {KeyLength} bytes.", nameof(key));
        if (nonce.Length != NonceLength)
            throw new ArgumentException($"The nonce must be {NonceLength} bytes.", nameof(nonce));

        var blob = new byte[Overhead + plaintext.Length];
        s_magic.CopyTo(blob, 0);
        blob[s_magic.Length] = FileVersion;
        nonce.CopyTo(blob.AsSpan(HeaderLength));

        using var cipher = new XChaCha20Poly1305();
        var written = cipher.Encrypt(key, nonce, blob.AsSpan(0, HeaderLength), plaintext,
                                     blob.AsSpan(HeaderLength + NonceLength));
        if (written != plaintext.Length + CryptoConstants.Xchacha20Poly1305TagLen)
            throw new CryptographicException("The backup encryption wrote an unexpected length.");

        return blob;
    }

    /// <summary>Whether <paramref name="blob"/> starts like a static channel backup (magic only).</summary>
    public static bool LooksLikeBackup(ReadOnlySpan<byte> blob) =>
        blob.Length >= s_magic.Length && blob[..s_magic.Length].SequenceEqual(s_magic);

    /// <summary>Decrypts a backup written by <see cref="Encrypt(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>.</summary>
    /// <exception cref="ChannelBackupFormatException">Not a backup, an unknown file version or too short.</exception>
    /// <exception cref="ChannelBackupAuthenticationException">Another key, or the bytes were changed.</exception>
    public static byte[] Decrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> blob)
    {
        if (key.Length != KeyLength)
            throw new ArgumentException($"The backup key must be {KeyLength} bytes.", nameof(key));
        if (!LooksLikeBackup(blob))
            throw new ChannelBackupFormatException("Not a static channel backup (bad magic).");
        if (blob.Length < HeaderLength)
            throw new ChannelBackupFormatException("The channel backup is truncated.");
        if (blob[s_magic.Length] != FileVersion)
            throw new ChannelBackupFormatException($"Unknown channel backup file version {blob[s_magic.Length]}.");
        if (blob.Length < Overhead)
            throw new ChannelBackupFormatException("The channel backup is truncated.");

        var nonce = blob.Slice(HeaderLength, NonceLength);
        var ciphertext = blob[(HeaderLength + NonceLength)..];
        var plaintext = new byte[ciphertext.Length - CryptoConstants.Xchacha20Poly1305TagLen];
        try
        {
            using var cipher = new XChaCha20Poly1305();
            cipher.Decrypt(key, nonce, blob[..HeaderLength], ciphertext, plaintext);
        }
        catch (CryptographicException e)
        {
            throw new ChannelBackupAuthenticationException(
                "The channel backup does not decrypt with this node's key: it belongs to another node or was "
              + "changed.", e);
        }

        return plaintext;
    }
}