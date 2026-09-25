namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Payloads;

/// <summary>
/// Represents a gossip_timestamp_filter message (BOLT 7, type 265). The payload is kept as raw bytes.
/// </summary>
public sealed class GossipTimestampFilterMessage(GossipPayload payload) : GossipMessage(MessageTypes.GossipTimestampFilter, payload);