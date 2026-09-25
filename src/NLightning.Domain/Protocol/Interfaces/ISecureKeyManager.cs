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
}