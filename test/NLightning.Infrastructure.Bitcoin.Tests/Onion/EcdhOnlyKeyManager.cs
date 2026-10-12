namespace NLightning.Infrastructure.Bitcoin.Tests.Onion;

using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Crypto.Functions;

/// <summary>
/// A key manager that only offers node-key ECDH: every accessor that would hand the private key out throws.
/// </summary>
/// <remarks>Hand-written because Moq cannot set up methods with span parameters.</remarks>
internal sealed class EcdhOnlyKeyManager(PrivKey nodeKey) : ISecureKeyManager
{
    private readonly Ecdh _ecdh = new();

    public int EcdhCalls { get; private set; }

    public BitcoinKeyPath ChannelKeyPath => throw new NotSupportedException();
    public uint HeightOfBirth => throw new NotSupportedException();

    public ExtPrivKey GetNextChannelKey(out uint index) => throw new NotSupportedException();
    public ExtPrivKey GetChannelKeyAtIndex(uint index) => throw new NotSupportedException();
    public ExtPrivKey GetDepositP2TrKeyAtIndex(uint index, bool isChange) => throw new NotSupportedException();
    public ExtPrivKey GetDepositP2WpkhKeyAtIndex(uint index, bool isChange) => throw new NotSupportedException();

    public CryptoKeyPair GetNodeKeyPair() =>
        throw new InvalidOperationException("The node private key must not leave the key manager.");

    public CompactPubKey GetNodePubKey() => _ecdh.GenerateKeyPair(nodeKey).CompactPubKey;

    public void ComputeNodeSharedSecret(ReadOnlySpan<byte> publicKey, Span<byte> sharedSecret)
    {
        EcdhCalls++;
        _ecdh.SecP256K1Dh(nodeKey, publicKey, sharedSecret);
    }
}