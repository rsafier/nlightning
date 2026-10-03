namespace NLightning.Domain.Protocol.Onion.Models;

using Crypto.ValueObjects;

/// <summary>
/// One hop of a route, as input to onion construction.
/// </summary>
/// <remarks>
/// <see cref="Payload"/> is the serialized hop payload (a TLV stream) <b>without</b> the leading bigsize length: the
/// onion builder writes the length prefix itself. Keeping it as raw bytes lets unknown odd TLVs pass through verbatim
/// and keeps the Sphinx core independent of the serialization layer.
/// </remarks>
public sealed class OnionHop
{
    /// <summary>
    /// The public key of the node that will peel this layer (the node id, or a blinded node id).
    /// </summary>
    public CompactPubKey NodeId { get; }

    /// <summary>
    /// The serialized hop payload, without its bigsize length prefix.
    /// </summary>
    public ReadOnlyMemory<byte> Payload { get; }

    public OnionHop(CompactPubKey nodeId, ReadOnlyMemory<byte> payload)
    {
        NodeId = nodeId;
        Payload = payload;
    }
}