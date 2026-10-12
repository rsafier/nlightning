using System.Security.Cryptography;
using NBitcoin;

namespace NLightning.Integration.Tests.Docker.Mock;

using Domain.Bitcoin.Constants;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Enums;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Signers;
using Infrastructure.Crypto.Functions;

public class FakeSecureKeyManager : ISecureKeyManager
{
    private readonly ExtKey _nodeKey;
    private readonly ExtKey _p2TrKey;
    private readonly ExtKey _p2WpkhKey;

    private readonly KeyPath _channelKeyPath = new(KeyConstants.ChannelKeyPathString);
    private readonly KeyPath _depositP2TrKeyPath = new(KeyConstants.P2TrKeyPathString);
    private readonly KeyPath _depositP2WpkhKeyPath = new(KeyConstants.P2WpkhKeyPathString);

    private readonly object _lastUsedIndexLock = new();
    private uint _lastUsedIndex;

    public BitcoinKeyPath KeyPath => new([]);

    // ReSharper disable once UnassignedGetOnlyAutoProperty
    public BitcoinKeyPath ChannelKeyPath { get; }

    // ReSharper disable once UnassignedGetOnlyAutoProperty
    public uint HeightOfBirth { get; }

    public FakeSecureKeyManager()
    {
        _nodeKey = new ExtKey(new Key(), Network.RegTest.GenesisHash.ToBytes());
        _p2TrKey = new ExtKey(new Key(), Network.RegTest.GenesisHash.ToBytes());
        _p2WpkhKey = new ExtKey(new Key(), Network.RegTest.GenesisHash.ToBytes());
    }

    public ExtPrivKey GetNextChannelKey(out uint index)
    {
        lock (_lastUsedIndexLock)
        {
            _lastUsedIndex++;
            index = _lastUsedIndex;
        }

        var derivedKey = _nodeKey.Derive(_channelKeyPath.Derive(index));
        return derivedKey.ToBytes();
    }

    public ExtPrivKey GetKeyRingKeyAtIndex(int family, int index) =>
        _nodeKey.Derive(new KeyPath($"1017'/0'/{family}'/0/{index}")).ToBytes();

    public CompactPubKey GetKeyRingPublicKey(int family, int index) =>
        _nodeKey.Derive(new KeyPath($"1017'/0'/{family}'/0/{index}")).Neuter().PubKey.ToBytes();

    public ExtPrivKey GetChannelKeyAtIndex(uint index)
    {
        var derivedKey = _nodeKey.Derive(_channelKeyPath.Derive(index));
        return derivedKey.ToBytes();
    }

    public ExtPrivKey GetDepositP2TrKeyAtIndex(uint index, bool isChange)
    {
        return _p2TrKey.Derive(_depositP2TrKeyPath.Derive(isChange ? "1" : "0")).Derive(index).ToBytes();
    }

    public ExtPrivKey GetDepositP2WpkhKeyAtIndex(uint index, bool isChange)
    {
        return _p2WpkhKey.Derive(_depositP2WpkhKeyPath.Derive(isChange ? "1" : "0")).Derive(index).ToBytes();
    }

    public CryptoKeyPair GetNodeKeyPair()
    {
        return new CryptoKeyPair(_nodeKey.PrivateKey.ToBytes(), _nodeKey.PrivateKey.PubKey.ToBytes());
    }

    public CompactPubKey GetNodePubKey()
    {
        return _nodeKey.PrivateKey.PubKey.ToBytes();
    }

    public void ComputeNodeSharedSecret(ReadOnlySpan<byte> publicKey, Span<byte> sharedSecret)
    {
        var sharedPubKey = new PubKey(publicKey.ToArray()).GetSharedPubkey(_nodeKey.PrivateKey);
        SHA256.HashData(sharedPubKey.Compress().ToBytes(), sharedSecret);
    }

    public byte[] SignBolt11Invoice(string humanReadablePart, byte[] dataU5) =>
        LightningInvoiceSignature.Sign(_nodeKey.PrivateKey.ToBytes(), humanReadablePart, dataU5);

    public byte[] EncryptNodeData(NodeDataPurpose purpose, byte[] nonce, byte[] associatedData, byte[] plaintext) =>
        NodeAuxiliaryCrypto.Encrypt(_nodeKey.PrivateKey.ToBytes(), purpose, nonce, associatedData, plaintext);

    public byte[] DecryptNodeData(NodeDataPurpose purpose, byte[] nonce, byte[] associatedData, byte[] ciphertext) =>
        NodeAuxiliaryCrypto.Decrypt(_nodeKey.PrivateKey.ToBytes(), purpose, nonce, associatedData, ciphertext);

    public byte[] ComputeOfferPathId(byte[] offerMetadata) =>
        NodeAuxiliaryCrypto.ComputeOfferPathId(_nodeKey.PrivateKey.ToBytes(), offerMetadata);

    public CompactPubKey GetWalletPublicKey(uint index, bool isChange, AddressType addressType)
    {
        var key = addressType == AddressType.P2Tr
                      ? GetDepositP2TrKeyAtIndex(index, isChange)
                      : GetDepositP2WpkhKeyAtIndex(index, isChange);
        return ExtKey.CreateFromBytes(key).PrivateKey.PubKey.ToBytes();
    }

    public bool EnsureLastUsedChannelIndexAtLeast(uint highestUsedIndex)
    {
        lock (_lastUsedIndexLock)
        {
            if (_lastUsedIndex >= highestUsedIndex)
                return false;
            _lastUsedIndex = highestUsedIndex;
            return true;
        }
    }
}