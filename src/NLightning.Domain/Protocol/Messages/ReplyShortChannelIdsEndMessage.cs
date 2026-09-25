namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Payloads;

/// <summary>
/// Represents a reply_short_channel_ids_end message (BOLT 7, type 262).
/// </summary>
public sealed class ReplyShortChannelIdsEndMessage(ReplyShortChannelIdsEndPayload payload)
    : BaseMessage(MessageTypes.ReplyShortChannelIdsEnd, payload)
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new ReplyShortChannelIdsEndPayload Payload => (ReplyShortChannelIdsEndPayload)base.Payload;
}