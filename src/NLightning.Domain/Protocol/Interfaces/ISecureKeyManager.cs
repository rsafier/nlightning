namespace NLightning.Domain.Protocol.Interfaces;

using Bitcoin.ValueObjects;
using Crypto.ValueObjects;

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
}