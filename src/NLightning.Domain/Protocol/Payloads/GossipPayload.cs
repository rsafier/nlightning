namespace NLightning.Domain.Protocol.Payloads;

using Interfaces;
using Messages;

/// <summary>
/// Opaque payload of a BOLT 7 gossip message.
/// </summary>
/// <remarks>
/// Gossip is not implemented yet. The payload keeps the raw wire bytes that follow the message type (including any
/// TLV extension) so the message can be recognized, logged and dropped instead of killing the connection.
/// </remarks>
/// <seealso cref="GossipMessage"/>
public class GossipPayload(ReadOnlyMemory<byte> data) : IMessagePayload
{
    /// <summary>
    /// The raw bytes of the message after the type field.
    /// </summary>
    public ReadOnlyMemory<byte> Data { get; } = data;
}