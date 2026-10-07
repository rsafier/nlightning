using System.Security.Cryptography;

namespace NLightning.Infrastructure.Crypto.Functions;

using Ciphers;
using Domain.Protocol.Enums;

/// <summary>Fixed domain-separated auxiliary operations executed inside the key manager.</summary>
public static class NodeAuxiliaryCrypto
{
    public static byte[] Encrypt(ReadOnlySpan<byte> nodePrivateKey, NodeDataPurpose purpose,
                                 byte[] nonce, byte[] associatedData, byte[] plaintext)
    {
        var key = DeriveKey(nodePrivateKey, purpose);
        try
        {
            var ciphertext = new byte[plaintext.Length + 16];
            using var cipher = new XChaCha20Poly1305();
            cipher.Encrypt(key, nonce, associatedData, plaintext, ciphertext);
            return ciphertext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> nodePrivateKey, NodeDataPurpose purpose,
                                 byte[] nonce, byte[] associatedData, byte[] ciphertext)
    {
        if (ciphertext.Length < 16)
            throw new CryptographicException("The encrypted node data is truncated.");
        var key = DeriveKey(nodePrivateKey, purpose);
        var plaintext = new byte[ciphertext.Length - 16];
        try
        {
            using var cipher = new XChaCha20Poly1305();
            cipher.Decrypt(key, nonce, associatedData, ciphertext, plaintext);
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static byte[] ComputeOfferPathId(ReadOnlySpan<byte> nodePrivateKey, byte[] offerMetadata)
    {
        var secret = HMACSHA256.HashData(nodePrivateKey, "nltg_bolt12_offer_paths"u8);
        try
        {
            var label = "nltg_bolt12_offer_path"u8;
            var message = new byte[label.Length + offerMetadata.Length];
            label.CopyTo(message);
            offerMetadata.CopyTo(message, label.Length);
            return HMACSHA256.HashData(secret, message);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private static byte[] DeriveKey(ReadOnlySpan<byte> nodePrivateKey, NodeDataPurpose purpose)
    {
        if (nodePrivateKey.Length != 32)
            throw new ArgumentException("The node private key must be 32 bytes.", nameof(nodePrivateKey));
        switch (purpose)
        {
            case NodeDataPurpose.PeerStorage:
                return HMACSHA256.HashData(nodePrivateKey, "nltg peer storage v1"u8);
            case NodeDataPurpose.ChannelBackup:
                var backupKey = new byte[32];
                HKDF.DeriveKey(HashAlgorithmName.SHA256, nodePrivateKey, backupKey,
                               "NLightning static channel backup"u8, "nltg-scb-v1"u8);
                return backupKey;
            default:
                throw new ArgumentOutOfRangeException(nameof(purpose));
        }
    }
}