using System.Security.Cryptography;

namespace NLightning.Domain.Protocol.Interfaces;

using Bitcoin.Enums;
using Bitcoin.ValueObjects;
using Crypto.ValueObjects;
using Protocol.Enums;

public interface ISecureKeyManager
{
    BitcoinKeyPath ChannelKeyPath { get; }
    uint HeightOfBirth { get; }

    ExtPrivKey GetNextChannelKey(out uint index);
    ExtPrivKey GetChannelKeyAtIndex(uint index);
    ExtPrivKey GetDepositP2TrKeyAtIndex(uint index, bool isChange);
    ExtPrivKey GetDepositP2WpkhKeyAtIndex(uint index, bool isChange);

    /// <summary>
    /// Returns the node key pair.
    /// </summary>
    /// <remarks>
    /// The private key array is a fresh copy owned by the caller, who may (and should) zero it after use.
    /// </remarks>
    CryptoKeyPair GetNodeKeyPair();

    CompactPubKey GetNodePubKey();

    /// <summary>
    /// Computes the ECDH shared secret between the node key and <paramref name="publicKey"/>:
    /// <c>SHA256(compressed(node_key * publicKey))</c> (BOLT 4 Sphinx / BOLT 8).
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="GetNodeKeyPair"/>, the node private key never leaves the key manager.
    /// </remarks>
    /// <param name="publicKey">A 33-byte compressed secp256k1 public key.</param>
    /// <param name="sharedSecret">The 32-byte destination.</param>
    /// <exception cref="ArgumentException">If <paramref name="publicKey"/> is not a valid point.</exception>
    void ComputeNodeSharedSecret(ReadOnlySpan<byte> publicKey, Span<byte> sharedSecret);

    /// <summary>Signs BOLT 11 HRP and unsigned five-bit words; returns compact signature followed by recovery id.</summary>
    byte[] SignBolt11Invoice(string humanReadablePart, byte[] dataU5) =>
        throw new NotSupportedException("This key manager does not support invoice signing.");

    /// <summary>Derives a wallet public key without exposing its private key or chain code.</summary>
    CompactPubKey GetWalletPublicKey(uint index, bool isChange, AddressType addressType) =>
        throw new NotSupportedException("This key manager does not support public wallet derivation.");

    /// <summary>Encrypts with the fixed, domain-separated key for the selected node data purpose.</summary>
    byte[] EncryptNodeData(NodeDataPurpose purpose, byte[] nonce, byte[] associatedData, byte[] plaintext) =>
        throw new NotSupportedException("This key manager does not support node data encryption.");

    /// <summary>Authenticates and decrypts with the selected node data key; authentication failure throws.</summary>
    byte[] DecryptNodeData(NodeDataPurpose purpose, byte[] nonce, byte[] associatedData, byte[] ciphertext) =>
        throw new NotSupportedException("This key manager does not support node data decryption.");

    /// <summary>Computes the fixed BOLT 12 offer path identifier without exporting its secret.</summary>
    byte[] ComputeOfferPathId(byte[] offerMetadata)
    {
        // Compatibility for local key managers; remote implementations override this operation.
        var privateKey = GetNodeKeyPair().PrivKey.Value.ToArray();
        byte[]? secret = null;
        try
        {
            secret = HMACSHA256.HashData(privateKey, "nltg_bolt12_offer_paths"u8);
            var label = "nltg_bolt12_offer_path"u8;
            var message = new byte[label.Length + offerMetadata.Length];
            label.CopyTo(message);
            offerMetadata.CopyTo(message, label.Length);
            return HMACSHA256.HashData(secret, message);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
            if (secret is not null)
                CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>Durably reserves a fresh channel key index without exporting the key.</summary>
    uint ReserveChannelKeyIndex()
    {
        GetNextChannelKey(out var index);
        return index;
    }

    /// <summary>Raises the durable channel index floor, never lowering it; true when it was raised.</summary>
    bool EnsureLastUsedChannelIndexAtLeast(uint highestUsedIndex) =>
        throw new NotSupportedException("This key manager does not support index reconciliation.");
}