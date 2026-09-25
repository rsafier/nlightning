namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Payloads;

/// <summary>
/// Represents a reply_short_channel_ids_end message (BOLT 7, type 262). The payload is kept as raw bytes.
/// </summary>
public sealed class ReplyShortChannelIdsEndMessage(GossipPayload payload) : GossipMessage(MessageTypes.ReplyShortChannelIdsEnd, payload);