using System.Security.Cryptography;
using NBitcoin;

namespace NLightning.Application.Tests.Payments;

using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Enums;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Signers;
using Infrastructure.Crypto.Functions;

/// <summary>
/// A node key manager for payment tests: node key pair and node-key ECDH (BOLT 4 Sphinx) from a fixed seed byte.
/// </summary>
/// <remarks>Hand-written because Moq cannot set up methods with span parameters.</remarks>
internal sealed class TestNodeKeyManager : ISecureKeyManager
{
    private readonly Key _nodeKey;

    public TestNodeKeyManager(byte seed)
    {
        _nodeKey = new Key(Enumerable.Repeat(seed, 32).ToArray());
    }

    public CompactPubKey NodeId => new(_nodeKey.PubKey.ToBytes());

    public BitcoinKeyPath ChannelKeyPath => throw new NotSupportedException();
    public uint HeightOfBirth => throw new NotSupportedException();

    public ExtPrivKey GetNextChannelKey(out uint index) => throw new NotSupportedException();
    public ExtPrivKey GetChannelKeyAtIndex(uint index) => throw new NotSupportedException();
    public ExtPrivKey GetDepositP2TrKeyAtIndex(uint index, bool isChange) => throw new NotSupportedException();
    public ExtPrivKey GetDepositP2WpkhKeyAtIndex(uint index, bool isChange) => throw new NotSupportedException();

    public CryptoKeyPair GetNodeKeyPair() => new(new PrivKey(_nodeKey.ToBytes()), NodeId);

    public CompactPubKey GetNodePubKey() => NodeId;

    public void ComputeNodeSharedSecret(ReadOnlySpan<byte> publicKey, Span<byte> sharedSecret)
    {
        var sharedPoint = new PubKey(publicKey.ToArray()).GetSharedPubkey(_nodeKey);
        SHA256.HashData(sharedPoint.ToBytes(), sharedSecret);
    }

    public byte[] SignBolt11Invoice(string humanReadablePart, byte[] dataU5) =>
        LightningInvoiceSignature.Sign(_nodeKey.ToBytes(), humanReadablePart, dataU5);

    public byte[] EncryptNodeData(NodeDataPurpose purpose, byte[] nonce, byte[] associatedData, byte[] plaintext) =>
        NodeAuxiliaryCrypto.Encrypt(_nodeKey.ToBytes(), purpose, nonce, associatedData, plaintext);

    public byte[] DecryptNodeData(NodeDataPurpose purpose, byte[] nonce, byte[] associatedData, byte[] ciphertext) =>
        NodeAuxiliaryCrypto.Decrypt(_nodeKey.ToBytes(), purpose, nonce, associatedData, ciphertext);

    public byte[] ComputeOfferPathId(byte[] offerMetadata) =>
        NodeAuxiliaryCrypto.ComputeOfferPathId(_nodeKey.ToBytes(), offerMetadata);
}