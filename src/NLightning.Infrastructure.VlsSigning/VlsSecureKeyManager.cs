using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NLightning.Domain.Bitcoin.Enums;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Protocol.Enums;
using NLightning.Domain.Protocol.Interfaces;

namespace NLightning.Infrastructure.VlsSigning;

/// <summary>Stock VLS Native public keys and purpose-specific crypto. No secret key export.</summary>
public sealed class VlsSecureKeyManager(VlsSignerConnection connection, VlsChannelMappingRegistry? mappings = null) : ISecureKeyManager
{
    public BitcoinKeyPath ChannelKeyPath => new("vls-native"u8.ToArray());
    public uint HeightOfBirth => 0;
    public AddressType SupportedWalletAddressTypes => AddressType.P2Wpkh;
    public CompactPubKey GetNodePubKey() => connection.Identity.NodePublicKey;
    public ExtPrivKey GetNextChannelKey(out uint index) { index = 0; throw Unavailable(); }
    public ExtPrivKey GetChannelKeyAtIndex(uint index) => throw Unavailable();
    public ExtPrivKey GetDepositP2TrKeyAtIndex(uint index, bool isChange) => throw Unavailable();
    public ExtPrivKey GetDepositP2WpkhKeyAtIndex(uint index, bool isChange) => throw Unavailable();
    public CryptoKeyPair GetNodeKeyPair() => throw Unavailable();
    public uint ReserveChannelKeyIndex() => throw new NotSupportedException("VLS channel allocation requires the peer identity.");
    public bool EnsureLastUsedChannelIndexAtLeast(uint highestUsedIndex)
    {
        if (mappings is null) throw new InvalidOperationException("VLS mapping registry is required for startup reconciliation.");
        _ = mappings.GetByIndex(highestUsedIndex); return false;
    }
    public void ComputeNodeSharedSecret(ReadOnlySpan<byte> publicKey, Span<byte> sharedSecret)
    {
        if (publicKey.Length != 33 || sharedSecret.Length != 32) throw new ArgumentException("Invalid ECDH key or destination length.");
        var bytes = Convert.FromHexString(connection.Invoke(VlsOperations.Ecdh, new JsonObject { ["op"] = "ecdh", ["public_key"] = Convert.ToHexString(publicKey).ToLowerInvariant() }).GetProperty("secret").GetString()!);
        try { bytes.CopyTo(sharedSecret); } finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public byte[] SignBolt11Invoice(string humanReadablePart, byte[] dataU5) => Convert.FromHexString(connection.Invoke(VlsOperations.Invoice,
        new JsonObject { ["op"] = "sign_invoice", ["hrp"] = humanReadablePart, ["words"] = new JsonArray(dataU5.Select(b => JsonValue.Create(b)).Cast<JsonNode?>().ToArray()) }).GetProperty("signature").GetString()!);
    public CompactPubKey GetWalletPublicKey(uint index, bool isChange, AddressType addressType)
    {
        if (addressType != AddressType.P2Wpkh) throw new NotSupportedException("VLS prototype wallet supports P2WPKH only.");
        var account = NBitcoin.ExtPubKey.Parse(connection.WalletExtendedPublicKey, NBitcoin.Network.RegTest);
        return new CompactPubKey(account.Derive(WalletIndex(index, isChange)).PubKey.ToBytes());
    }
    public static uint WalletIndex(uint index, bool isChange)
    {
        var child = checked(index * 2 + (isChange ? 1u : 0u));
        if (child >= 0x80000000) throw new ArgumentOutOfRangeException(nameof(index));
        return child;
    }
    public byte[] EncryptNodeData(NodeDataPurpose purpose, byte[] nonce, byte[] associatedData, byte[] plaintext) => throw new NotSupportedException("VLS prototype has no node auxiliary encryption.");
    public byte[] DecryptNodeData(NodeDataPurpose purpose, byte[] nonce, byte[] associatedData, byte[] ciphertext) => throw new NotSupportedException("VLS prototype has no node auxiliary encryption.");
    public byte[] ComputeOfferPathId(byte[] offerMetadata) => throw new NotSupportedException("VLS prototype has no BOLT12 support.");
    private static NotSupportedException Unavailable() => new("Private keys remain in VLS.");
}