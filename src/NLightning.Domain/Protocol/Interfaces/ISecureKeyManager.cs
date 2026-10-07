namespace NLightning.Domain.Protocol.Interfaces;

using Bitcoin.ValueObjects;
using Crypto.ValueObjects;

public interface ISecureKeyManager
{
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
}

/// <summary>A deposit wallet account's public description (<see cref="ISecureKeyManager.GetDepositAccount"/>).</summary>
/// <param name="ExtendedPublicKey">The account's extended public key (BIP32 serialization for the network).</param>
/// <param name="DerivationPath">The account's path from the master key, e.g. <c>m/84'/0'/0'</c>.</param>
/// <param name="MasterFingerprint">The master key's fingerprint, big-endian.</param>
public sealed record DepositAccountInfo(string ExtendedPublicKey, string DerivationPath, byte[] MasterFingerprint);