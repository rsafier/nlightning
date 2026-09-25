using System.Security.Cryptography;

namespace NLightning.Infrastructure.Bitcoin.Onion;

using Domain.Crypto.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.ValueObjects;
using Infrastructure.Crypto.Interfaces;

/// <summary>
/// BOLT 4 Sphinx facade over <see cref="OnionBuilder"/> and <see cref="OnionPeeler"/>.
/// </summary>
/// <remarks>
/// Stateless and thread-safe: every call allocates its own hash and stream state. The node key for
/// <see cref="PeelAsLocalNode"/> comes from <see cref="ISecureKeyManager"/>, which
/// is optional so the service can be resolved where no key manager is registered.
/// </remarks>
internal sealed class SphinxService : ISphinxService
{
    /// <summary>
    /// BOLT 4: a payment hop payload length of 0 (legacy) or 1 (reserved) is invalid.
    /// </summary>
    private const int MinPaymentPayloadLength = 2;

    private readonly OnionBuilder _builder;
    private readonly OnionPeeler _peeler;
    private readonly ISecureKeyManager? _secureKeyManager;

    public SphinxService(IEcdh ecdh, ISecp256K1Math secp256K1Math, ISecureKeyManager? secureKeyManager = null)
    {
        _builder = new OnionBuilder(ecdh, secp256K1Math);
        _peeler = new OnionPeeler(ecdh, secp256K1Math);
        _secureKeyManager = secureKeyManager;
    }

    /// <inheritdoc/>
    public OnionPacket Construct(IReadOnlyList<OnionHop> hops, PrivKey sessionKey, ReadOnlySpan<byte> associatedData,
                                 int hopPayloadsLength = OnionConstants.HopPayloadsLength,
                                 OnionPacketKind packetKind = OnionPacketKind.Payment)
    {
        return _builder.Build(hops, sessionKey, associatedData, hopPayloadsLength, GetMinPayloadLength(packetKind));
    }

    /// <inheritdoc/>
    public ConstructedOnion ConstructWithSharedSecrets(IReadOnlyList<OnionHop> hops, PrivKey sessionKey,
                                                       ReadOnlySpan<byte> associatedData,
                                                       int hopPayloadsLength = OnionConstants.HopPayloadsLength,
                                                       OnionPacketKind packetKind = OnionPacketKind.Payment)
    {
        return _builder.BuildWithSharedSecrets(hops, sessionKey, associatedData, hopPayloadsLength,
                                               GetMinPayloadLength(packetKind));
    }

    /// <inheritdoc/>
    public IReadOnlyList<Secret> ComputeSharedSecrets(IReadOnlyList<CompactPubKey> nodeIds, PrivKey sessionKey)
    {
        var (_, sharedSecrets) = _builder.ComputeHopKeys(nodeIds, sessionKey);
        return sharedSecrets.Select(secret => new Secret(secret)).ToList();
    }

    /// <inheritdoc/>
    public PeeledOnion PeelAsLocalNode(OnionPacket packet, ReadOnlySpan<byte> associatedData,
                                       CompactPubKey? pathKey = null,
                                       OnionPacketKind packetKind = OnionPacketKind.Payment)
    {
        if (_secureKeyManager is null)
            throw new InvalidOperationException("No secure key manager is available to peel with the node key.");

        // GetNodeKeyPair returns a fresh copy of the node key; wipe it so each HTLC does not leave one on the heap.
        var nodeKey = _secureKeyManager.GetNodeKeyPair().PrivKey;
        try
        {
            return Peel(packet, associatedData, nodeKey, pathKey, packetKind);
        }
        finally
        {
            if (nodeKey.Value is not null)
                CryptographicOperations.ZeroMemory(nodeKey.Value);
        }
    }

    /// <inheritdoc/>
    public PeeledOnion Peel(OnionPacket packet, ReadOnlySpan<byte> associatedData, PrivKey nodeKey,
                            CompactPubKey? pathKey = null, OnionPacketKind packetKind = OnionPacketKind.Payment)
    {
        return _peeler.Peel(packet, associatedData, nodeKey, pathKey, GetMinPayloadLength(packetKind),
                            packetKind == OnionPacketKind.Payment && pathKey.HasValue);
    }

    private static int GetMinPayloadLength(OnionPacketKind packetKind)
    {
        return packetKind switch
        {
            OnionPacketKind.Payment => MinPaymentPayloadLength,
            OnionPacketKind.OnionMessage => 0,
            _ => throw new ArgumentOutOfRangeException(nameof(packetKind), packetKind, "Unknown onion packet kind.")
        };
    }
}