namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Payloads;

/// <summary>
/// Represents a reply_channel_range message (BOLT 7, type 264). The payload is kept as raw bytes.
/// </summary>
public sealed class ReplyChannelRangeMessage(GossipPayload payload) : GossipMessage(MessageTypes.ReplyChannelRange, payload);