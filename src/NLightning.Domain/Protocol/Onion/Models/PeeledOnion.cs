namespace NLightning.Domain.Protocol.Onion.Models;

using Crypto.ValueObjects;
using ValueObjects;

/// <summary>
/// The result of peeling one layer of an onion packet.
/// </summary>
/// <remarks>
/// <see cref="Payload"/> holds the raw hop payload (the TLV stream, without its bigsize length prefix). Parsing it
/// into typed TLVs is done by the serialization layer.
/// </remarks>
public sealed class PeeledOnion
{
    /// <summary>
    /// The raw hop payload for this node (TLV stream without the bigsize length prefix).
    /// </summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>
    /// The shared secret between this node and the sender. It is needed to create or wrap a failure for this HTLC.
    /// </summary>
    public Secret SharedSecret { get; }

    /// <summary>
    /// The packet to forward to the next hop, or <c>null</c> when this node is the final destination.
    /// </summary>
    public OnionPacket? NextPacket { get; }

    /// <summary>
    /// Whether this node is the final destination (the next HMAC is all zero).
    /// </summary>
    public bool IsFinal => NextPacket is null;

    public PeeledOnion(ReadOnlyMemory<byte> payload, Secret sharedSecret, OnionPacket? nextPacket)
    {
        Payload = payload;
        SharedSecret = sharedSecret;
        NextPacket = nextPacket;
    }
}