namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Payloads;

/// <summary>
/// Represents a gossip_timestamp_filter message (BOLT 7, type 265).
/// </summary>
public sealed class GossipTimestampFilterMessage(GossipTimestampFilterPayload payload)
    : BaseMessage(MessageTypes.GossipTimestampFilter, payload)
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new GossipTimestampFilterPayload Payload => (GossipTimestampFilterPayload)base.Payload;
}