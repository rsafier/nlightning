using NLightning.Domain.Bitcoin.Enums;
using NLightning.Domain.Bitcoin.SilentPayments.Interfaces;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Protocol.Enums;
using NLightning.Domain.Protocol.Interfaces;

namespace NLightning.Infrastructure.RemoteSigning;

/// <summary>Remote key manager exports public information and purpose-scoped crypto only.</summary>
public sealed class RemoteSecureKeyManager(RemoteSignerConnection connection) : ISecureKeyManager, ISilentPaymentKeySource
{
    private readonly RemoteSilentPaymentKeySource _silentPaymentKeys = new(connection);
    public NLightning.Domain.Signing.NodeSigningContext Context => connection.Context;
    public CompactPubKey ScanPubKey => _silentPaymentKeys.ScanPubKey;
    public CompactPubKey SpendPubKey => _silentPaymentKeys.SpendPubKey;
    public bool RecoverableElsewhere => _silentPaymentKeys.RecoverableElsewhere;
    public void ComputeScanSharedSecret(ReadOnlySpan<byte> input, Span<byte> point) => _silentPaymentKeys.ComputeScanSharedSecret(input, point);
    public void GetLabelTweak(uint label, Span<byte> scalar) => _silentPaymentKeys.GetLabelTweak(label, scalar);
    public CompactPubKey GetLabelPoint(uint label) => _silentPaymentKeys.GetLabelPoint(label);
    public BitcoinKeyPath ChannelKeyPath => connection.Identity.ChannelKeyPath;
    public uint HeightOfBirth => connection.Identity.HeightOfBirth;
    public CompactPubKey GetNodePubKey() => connection.Identity.NodePublicKey;
    public ExtPrivKey GetNextChannelKey(out uint index) { index = 0; throw PrivateKeyUnavailable(); }
    public ExtPrivKey GetChannelKeyAtIndex(uint index) => throw PrivateKeyUnavailable();
    public ExtPrivKey GetDepositP2TrKeyAtIndex(uint index, bool isChange) => throw PrivateKeyUnavailable();
    public ExtPrivKey GetDepositP2WpkhKeyAtIndex(uint index, bool isChange) => throw PrivateKeyUnavailable();
    public ExtPrivKey GetDepositKeyAtIndex(AddressType addressType, uint accountIndex, uint index, bool isChange) => throw PrivateKeyUnavailable();
    public ExtPrivKey GetKeyRingKeyAtIndex(int family, int index) => throw PrivateKeyUnavailable();
    public CompactPubKey GetKeyRingPublicKey(int family, int index) =>
        Read<CompactPubKey>(SignerOperations.GetKeyRingPublicKey, family, index);
    public byte[] GetSilentPaymentSpendKey(ReadOnlySpan<byte> tweak32, uint? label) => throw PrivateKeyUnavailable();
    public DepositAccountInfo? GetDepositAccount(AddressType addressType)
    {
        var element = connection.Invoke(SignerOperations.GetDepositAccount, addressType)[0];
        return element.ValueKind == System.Text.Json.JsonValueKind.Null ? null : SignerWire.Read<DepositAccountInfo>(element);
    }
    public DepositAccountInfo? GetDepositAccount(AddressType addressType, uint accountIndex)
    {
        var element = connection.Invoke(SignerOperations.GetDepositAccount2, addressType, accountIndex)[0];
        return element.ValueKind == System.Text.Json.JsonValueKind.Null ? null : SignerWire.Read<DepositAccountInfo>(element);
    }
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