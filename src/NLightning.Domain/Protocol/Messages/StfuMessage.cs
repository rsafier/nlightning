namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Payloads;

/// <summary>
/// Represents a stfu message (BOLT 2 "Channel Quiescence", type 2: <c>channel_id</c> ‖ <c>u8 initiator</c>).
/// </summary>
/// <remarks>
/// The stfu message means SomeThing Fundamental is Underway, so we kindly ask the other node to STFU because we have
/// something important to say. It is a channel message (splicing plan D3, Q-W-01): once <c>PeerService</c> stops
/// intercepting it (lane Q-A, NL-019) it is routed by <c>ChannelManager</c> under the channel's lock like every other
/// channel message.
/// The message type is 2.
/// </remarks>
/// <param name="payload">The stfu payload.</param>
public sealed class StfuMessage(StfuPayload payload) : BaseChannelMessage(MessageTypes.Stfu, payload)
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new StfuPayload Payload { get => (StfuPayload)base.Payload; }
}