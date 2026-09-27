namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Models;
using Payloads;
using Tlv;

/// <summary>
/// Represents a channel_reestablish message.
/// </summary>
/// <remarks>
/// The channel_reestablish message is sent when a connection is lost.
/// The message type is 136.
/// </remarks>
public sealed class ChannelReestablishMessage : BaseChannelMessage
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new ChannelReestablishPayload Payload { get => (ChannelReestablishPayload)base.Payload; }

    public NextFundingTlv? NextFundingTlv { get; }

    /// <summary>
    /// BOLT 2 <c>channel_reestablish_tlvs</c> type 5 (<c>my_current_funding_locked</c>, SP-RE-02). Not read or written
    /// by the serializer until lane SP1-A-T2.
    /// </summary>
    public MyCurrentFundingLockedTlv? MyCurrentFundingLockedTlv { get; }

    public ChannelReestablishMessage(ChannelReestablishPayload payload, NextFundingTlv? nextFundingTlv = null,
                                     MyCurrentFundingLockedTlv? myCurrentFundingLockedTlv = null)
        : base(MessageTypes.ChannelReestablish, payload)
    {
        NextFundingTlv = nextFundingTlv;
        MyCurrentFundingLockedTlv = myCurrentFundingLockedTlv;

        if (NextFundingTlv is null && MyCurrentFundingLockedTlv is null)
            return;

        Extension = new TlvStream();
        if (NextFundingTlv is not null)
            Extension.Add(NextFundingTlv);
        if (MyCurrentFundingLockedTlv is not null)
            Extension.Add(MyCurrentFundingLockedTlv);
    }
}