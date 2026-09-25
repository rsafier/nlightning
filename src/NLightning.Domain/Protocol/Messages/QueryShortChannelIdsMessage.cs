namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Payloads;

/// <summary>
/// Represents a query_short_channel_ids message (BOLT 7, type 261). The payload is kept as raw bytes.
/// </summary>
public sealed class QueryShortChannelIdsMessage(GossipPayload payload) : GossipMessage(MessageTypes.QueryShortChannelIds, payload);