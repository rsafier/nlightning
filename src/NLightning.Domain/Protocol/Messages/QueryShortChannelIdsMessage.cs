namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Models;
using Payloads;
using Tlv;

/// <summary>
/// Represents a query_short_channel_ids message (BOLT 7, type 261).
/// </summary>
public sealed class QueryShortChannelIdsMessage : BaseMessage
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new QueryShortChannelIdsPayload Payload => (QueryShortChannelIdsPayload)base.Payload;

    /// <summary>
    /// The raw <c>query_flags</c> TLV (encoding type byte followed by one bigsize flag per short_channel_id), if any.
    /// </summary>
    public BaseTlv? QueryFlagsTlv { get; }

    public QueryShortChannelIdsMessage(QueryShortChannelIdsPayload payload, BaseTlv? queryFlagsTlv = null)
        : base(MessageTypes.QueryShortChannelIds, payload)
    {
        QueryFlagsTlv = queryFlagsTlv;

        if (queryFlagsTlv is not null)
        {
            Extension = new TlvStream();
            Extension.Add(queryFlagsTlv);
        }
    }
}