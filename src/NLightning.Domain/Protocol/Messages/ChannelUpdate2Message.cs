namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Payloads;

/// <summary>
/// Represents a channel_update_2 message (taproot gossip, BOLTs PR #1059, type 271).
/// </summary>
/// <remarks>
/// A pure TLV message signed with the origin's node key (BIP 340) over
/// <see cref="ChannelUpdate2Payload.GetSignatureHash"/>; its timestamp is a block height.
/// </remarks>
public sealed class ChannelUpdate2Message(ChannelUpdate2Payload payload)
    : BaseMessage(MessageTypes.ChannelUpdate2, payload)
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new ChannelUpdate2Payload Payload => (ChannelUpdate2Payload)base.Payload;
}