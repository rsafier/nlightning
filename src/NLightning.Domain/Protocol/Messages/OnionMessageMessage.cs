namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Payloads;

/// <summary>
/// <c>onion_message</c> (type 513, BOLT 4 "Onion Messages"): a Sphinx onion routed over the peer graph with no HTLC.
/// It is not a channel message and is only exchanged when <c>option_onion_messages</c> (BOLT 9 bits 38/39) was
/// negotiated.
/// </summary>
public sealed class OnionMessageMessage(OnionMessagePayload payload) : BaseMessage(MessageTypes.OnionMessage, payload)
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new OnionMessagePayload Payload => (OnionMessagePayload)base.Payload;
}