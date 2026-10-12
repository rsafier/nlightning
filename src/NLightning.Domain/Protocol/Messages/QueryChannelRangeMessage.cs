namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Models;
using Payloads;
using Tlv;

/// <summary>
/// Represents a query_channel_range message (BOLT 7, type 263).
/// </summary>
public sealed class QueryChannelRangeMessage : BaseMessage
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new QueryChannelRangePayload Payload => (QueryChannelRangePayload)base.Payload;

    /// <summary>
    /// The raw <c>query_option</c> TLV (a bigsize bitfield), if any.
    /// </summary>
    public BaseTlv? QueryOptionTlv { get; }

    public QueryChannelRangeMessage(QueryChannelRangePayload payload, BaseTlv? queryOptionTlv = null)
        : base(MessageTypes.QueryChannelRange, payload)
    {
        QueryOptionTlv = queryOptionTlv;

        if (queryOptionTlv is not null)
        {
            Extension = new TlvStream();
            Extension.Add(queryOptionTlv);
        }
    }
}