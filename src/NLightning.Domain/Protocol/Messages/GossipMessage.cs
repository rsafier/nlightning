namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Payloads;

/// <summary>
/// Base class for BOLT 7 gossip messages that are recognized but not parsed.
/// </summary>
/// <remarks>
/// The payload is kept as raw bytes. Recognizing these types keeps the even gossip types (256, 258, 262, 264) from
/// being treated as unknown even messages, which would disconnect the peer.
/// </remarks>
public abstract class GossipMessage : BaseMessage
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new GossipPayload Payload => (GossipPayload)base.Payload;

    protected GossipMessage(MessageTypes type, GossipPayload payload) : base(type, payload)
    {
    }
}