namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Models;
using Payloads;
using Tlv;

/// <summary>
/// Represents a reply_channel_range message (BOLT 7, type 264).
/// </summary>
public sealed class ReplyChannelRangeMessage : BaseMessage
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new ReplyChannelRangePayload Payload => (ReplyChannelRangePayload)base.Payload;

    /// <summary>
    /// The raw <c>timestamps_tlv</c>, if any.
    /// </summary>
    public BaseTlv? TimestampsTlv { get; }

    /// <summary>
    /// The raw <c>checksums_tlv</c>, if any.
    /// </summary>
    public BaseTlv? ChecksumsTlv { get; }

    public ReplyChannelRangeMessage(ReplyChannelRangePayload payload, BaseTlv? timestampsTlv = null,
                                    BaseTlv? checksumsTlv = null)
        : base(MessageTypes.ReplyChannelRange, payload)
    {
        TimestampsTlv = timestampsTlv;
        ChecksumsTlv = checksumsTlv;

        if (timestampsTlv is not null || checksumsTlv is not null)
        {
            Extension = new TlvStream();
            Extension.Add(timestampsTlv, checksumsTlv);
        }
    }
}