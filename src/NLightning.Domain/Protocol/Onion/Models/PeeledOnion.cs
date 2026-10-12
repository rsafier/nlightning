namespace NLightning.Domain.Protocol.Onion.Models;

using Crypto.ValueObjects;
using ValueObjects;

/// <summary>
/// The result of peeling one layer of an onion packet.
/// </summary>
/// <remarks>
/// <see cref="Payload"/> holds the raw hop payload (the TLV stream, without its bigsize length prefix). Parsing it
/// into typed TLVs is done by the serialization layer, whose <c>invalid_onion_payload</c> offsets count the stripped
/// (canonical, so recomputable) length prefix, as BOLT 4 measures them in the decrypted byte stream.
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
    /// The route-blinding shared secret <c>ECDH(path_key, node_key)</c>, or <c>null</c> when no path_key was given.
    /// </summary>
    /// <remarks>
    /// The route-blinding processor needs it to derive <c>rho</c> for decrypting <c>encrypted_recipient_data</c> and
    /// the next path_key (<c>SHA256(path_key || ss) * path_key</c>) without redoing the ECDH with the node key.
    /// </remarks>
    public Secret? PathKeySharedSecret { get; }

    /// <summary>
    /// Whether this node is the final destination (the next HMAC is all zero).
    /// </summary>
    public bool IsFinal => NextPacket is null;

    public PeeledOnion(ReadOnlyMemory<byte> payload, Secret sharedSecret, OnionPacket? nextPacket,
                       Secret? pathKeySharedSecret = null)
    {
        Payload = payload;
        SharedSecret = sharedSecret;
        NextPacket = nextPacket;
        PathKeySharedSecret = pathKeySharedSecret;
    }
}