using System.Security.Cryptography;

namespace NLightning.Domain.Protocol.Interfaces;

using Bitcoin.Enums;
using Bitcoin.ValueObjects;
using Crypto.ValueObjects;
using Protocol.Enums;

public interface ISecureKeyManager
{
    /// <summary>The wallet address types this signing backend can derive and spend.</summary>
    AddressType SupportedWalletAddressTypes => AddressType.P2Wpkh | AddressType.P2Tr;

    /// <summary>Derives an isolated swap key, preserving the key file's master derivation version.</summary>
    /// <remarks>Only Infrastructure.Bitcoin consumers may use this private material; RPCs expose public keys only.</remarks>
    ExtPrivKey GetKeyRingKeyAtIndex(int family, int index) =>
        throw new NotSupportedException("This key manager has no isolated key ring.");

    /// <summary>Returns a fresh raw BIP 352 output private scalar for the local signer, without a BIP86 tweak.</summary>
    /// <remarks>The caller must zero the returned array after use. The scan private key is never returned.</remarks>
    byte[] GetSilentPaymentSpendKey(ReadOnlySpan<byte> tweak32, uint? label) =>
        throw new NotSupportedException("This key manager has no silent payment keys.");

    BitcoinKeyPath ChannelKeyPath { get; }
    uint HeightOfBirth { get; }

    ExtPrivKey GetNextChannelKey(out uint index);
    /// <summary>
    /// The extended channel key at <paramref name="index"/>, as a fresh copy on every call: the signer zeroes it once
    /// it has derived what it needs (NL-911), so an implementation must never hand out an array it keeps.
    /// </summary>
    ExtPrivKey GetChannelKeyAtIndex(uint index);
    ExtPrivKey GetDepositP2TrKeyAtIndex(uint index, bool isChange);
    ExtPrivKey GetDepositP2WpkhKeyAtIndex(uint index, bool isChange);

    /// <summary>
    /// The deposit wallet's account of <paramref name="addressType"/> (P2WPKH or P2TR): its extended public key
    /// serialized for the node's network (BIP32 <c>xpub</c>/<c>tpub</c>), its derivation path and the master key
    /// fingerprint (big-endian, as BIP32 serializes it); null when this manager keeps no such account. Public data only
    /// (LND's <c>ListAccounts</c>, NL-1247).
    /// </summary>
    DepositAccountInfo? GetDepositAccount(Bitcoin.Enums.AddressType addressType) => null;

    /// <summary>Returns the public description of an isolated named BIP84/86 account.</summary>
    DepositAccountInfo? GetDepositAccount(Bitcoin.Enums.AddressType addressType, uint accountIndex) =>
        accountIndex == 0 ? GetDepositAccount(addressType)
                         : throw new NotSupportedException("This key manager has no named wallet accounts.");

    /// <summary>Returns a fresh private key copy for a local account's child; the caller must wipe its bytes.</summary>
    ExtPrivKey GetDepositKeyAtIndex(Bitcoin.Enums.AddressType addressType, uint accountIndex, uint index,
                                   bool isChange)
    {
        if (accountIndex != 0)
            throw new NotSupportedException("This key manager has no named wallet accounts.");
        return addressType switch
        {
            Bitcoin.Enums.AddressType.P2Wpkh => GetDepositP2WpkhKeyAtIndex(index, isChange),
            Bitcoin.Enums.AddressType.P2Tr => GetDepositP2TrKeyAtIndex(index, isChange),
            _ => throw new ArgumentOutOfRangeException(nameof(addressType))
        };
    }

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

/// <summary>A deposit wallet account's public description (<see cref="ISecureKeyManager.GetDepositAccount"/>).</summary>
/// <param name="ExtendedPublicKey">The account's extended public key (BIP32 serialization for the network).</param>
/// <param name="DerivationPath">The account's path from the master key, e.g. <c>m/84'/0'/0'</c>.</param>
/// <param name="MasterFingerprint">The master key's fingerprint, big-endian.</param>
public sealed record DepositAccountInfo(string ExtendedPublicKey, string DerivationPath, byte[] MasterFingerprint);