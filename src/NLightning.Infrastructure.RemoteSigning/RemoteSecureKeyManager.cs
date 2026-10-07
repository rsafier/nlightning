using NLightning.Domain.Bitcoin.Enums;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Protocol.Enums;
using NLightning.Domain.Protocol.Interfaces;

namespace NLightning.Infrastructure.RemoteSigning;

/// <summary>Remote key manager exports public information and purpose-scoped crypto only.</summary>
public sealed class RemoteSecureKeyManager(RemoteSignerConnection connection) : ISecureKeyManager
{
    public BitcoinKeyPath ChannelKeyPath => connection.Identity.ChannelKeyPath;
    public uint HeightOfBirth => connection.Identity.HeightOfBirth;
    public CompactPubKey GetNodePubKey() => connection.Identity.NodePublicKey;
    public ExtPrivKey GetNextChannelKey(out uint index) { index = 0; throw PrivateKeyUnavailable(); }
    public ExtPrivKey GetChannelKeyAtIndex(uint index) => throw PrivateKeyUnavailable();
    public ExtPrivKey GetDepositP2TrKeyAtIndex(uint index, bool isChange) => throw PrivateKeyUnavailable();
    public ExtPrivKey GetDepositP2WpkhKeyAtIndex(uint index, bool isChange) => throw PrivateKeyUnavailable();
    public CryptoKeyPair GetNodeKeyPair() => throw PrivateKeyUnavailable();
    private static NotSupportedException PrivateKeyUnavailable() => new("Private key material remains in the signer daemon.");
    public void ComputeNodeSharedSecret(ReadOnlySpan<byte> publicKey, Span<byte> sharedSecret)
    {
        if (sharedSecret.Length != 32) throw new ArgumentException("Shared secret destination must be 32 bytes.");
        var secret = SignerWire.Read<byte[]>(connection.Invoke(SignerOperations.ComputeNodeSharedSecret, publicKey.ToArray())[0]);
        try { secret.CopyTo(sharedSecret); } finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret); }
    }
    public byte[] SignBolt11Invoice(string humanReadablePart, byte[] dataU5) => Read<byte[]>(SignerOperations.SignBolt11Invoice, humanReadablePart, dataU5);
    public CompactPubKey GetWalletPublicKey(uint index, bool isChange, AddressType addressType) => Read<CompactPubKey>(SignerOperations.GetWalletPublicKey, index, isChange, addressType);
    public byte[] EncryptNodeData(NodeDataPurpose purpose, byte[] nonce, byte[] associatedData, byte[] plaintext) => Read<byte[]>(SignerOperations.EncryptNodeData, purpose, nonce, associatedData, plaintext);
    public byte[] DecryptNodeData(NodeDataPurpose purpose, byte[] nonce, byte[] associatedData, byte[] ciphertext) => Read<byte[]>(SignerOperations.DecryptNodeData, purpose, nonce, associatedData, ciphertext);
    public byte[] ComputeOfferPathId(byte[] offerMetadata) => Read<byte[]>(SignerOperations.ComputeOfferPathId, offerMetadata);
    public uint ReserveChannelKeyIndex() => Read<uint>(SignerOperations.ReserveChannelKeyIndex);
    public bool EnsureLastUsedChannelIndexAtLeast(uint highestUsedIndex) => Read<bool>(SignerOperations.EnsureLastUsedChannelIndexAtLeast, highestUsedIndex);
    private T Read<T>(uint op, params object?[] args) => SignerWire.Read<T>(connection.Invoke(op, args)[0]);
}