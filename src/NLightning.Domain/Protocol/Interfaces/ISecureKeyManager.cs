namespace NLightning.Domain.Protocol.Interfaces;

using Bitcoin.ValueObjects;
using Crypto.ValueObjects;

public interface ISecureKeyManager
{
    /// <summary>Derives an isolated swap key, preserving the key file's master derivation version.</summary>
    /// <remarks>Only Infrastructure.Bitcoin consumers may use this private material; RPCs expose public keys only.</remarks>
    ExtPrivKey GetKeyRingKeyAtIndex(int family, int index) =>
        throw new NotSupportedException("This key manager has no isolated key ring.");

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