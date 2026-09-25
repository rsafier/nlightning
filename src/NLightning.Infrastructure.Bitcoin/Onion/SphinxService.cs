namespace NLightning.Infrastructure.Bitcoin.Onion;

using Domain.Crypto.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.ValueObjects;

/// <summary>
/// BOLT 4 Sphinx facade over <see cref="OnionBuilder"/> and <see cref="OnionPeeler"/>.
/// </summary>
/// <remarks>
/// Stateless and thread-safe: every call allocates its own hash and stream state. <see cref="PeelAsLocalNode"/> does
/// its node-key ECDH through <see cref="ISecureKeyManager.ComputeNodeSharedSecret"/>; the key manager is optional so the
/// service can be resolved where none is registered.
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

    public SphinxService(ISecp256K1Math secp256K1Math, ISecureKeyManager? secureKeyManager = null)
    {
        _builder = new OnionBuilder();
        _peeler = new OnionPeeler(secp256K1Math);
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
        var (_, sharedSecrets) = OnionBuilder.ComputeHopKeys(nodeIds, sessionKey);
        return sharedSecrets.Select(secret => new Secret(secret)).ToList();
    }

    /// <inheritdoc/>
    public PeeledOnion PeelAsLocalNode(OnionPacket packet, ReadOnlySpan<byte> associatedData,
                                       CompactPubKey? pathKey = null,
                                       OnionPacketKind packetKind = OnionPacketKind.Payment)
    {
        if (_secureKeyManager is null)
            throw new InvalidOperationException("No secure key manager is available to peel with the node key.");

        // Every node-key operation is an ECDH done inside the key manager, so the node key is never copied out
        // (nor its public key re-derived) per HTLC. Route blinding tweaks the ephemeral key instead of the node key.
        return _peeler.Peel(packet, associatedData, _secureKeyManager.ComputeNodeSharedSecret, pathKey,
                            GetMinPayloadLength(packetKind), IsBlindedPayment(pathKey, packetKind));
    }

    /// <inheritdoc/>
    public PeeledOnion Peel(OnionPacket packet, ReadOnlySpan<byte> associatedData, PrivKey nodeKey,
                            CompactPubKey? pathKey = null, OnionPacketKind packetKind = OnionPacketKind.Payment)
    {
        return _peeler.Peel(packet, associatedData, nodeKey, pathKey, GetMinPayloadLength(packetKind),
                            IsBlindedPayment(pathKey, packetKind));
    }

    private static bool IsBlindedPayment(CompactPubKey? pathKey, OnionPacketKind packetKind)
    {
        return packetKind == OnionPacketKind.Payment && pathKey.HasValue;
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