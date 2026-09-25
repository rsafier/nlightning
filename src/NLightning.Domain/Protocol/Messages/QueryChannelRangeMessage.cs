namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Payloads;

/// <summary>
/// Represents a query_channel_range message (BOLT 7, type 263). The payload is kept as raw bytes.
/// </summary>
public sealed class QueryChannelRangeMessage(GossipPayload payload) : GossipMessage(MessageTypes.QueryChannelRange, payload);