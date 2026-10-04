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

    /// <summary>
    /// Simple taproot channels <c>next_local_nonces</c> (TLV 22): the sender's verification nonces for its next
    /// commitment, one per active funding (and, as Eclair 0.14.3 does, one for a pending splice or RBF attempt).
    /// Required on a simple taproot channel, absent otherwise.
    /// </summary>
    public NextLocalNoncesTlv? NextLocalNoncesTlv { get; }

    /// <summary>
    /// BOLTs PR #1324 <c>current_commit_nonce</c> (TLV 24): sent while the sender still misses the peer's
    /// <c>commitment_signed</c> for an interactive transaction of a simple taproot channel.
    /// </summary>
    public CurrentCommitNonceTlv? CurrentCommitNonceTlv { get; }

    public ChannelReestablishMessage(ChannelReestablishPayload payload, NextFundingTlv? nextFundingTlv = null,
                                     MyCurrentFundingLockedTlv? myCurrentFundingLockedTlv = null,
                                     NextLocalNoncesTlv? nextLocalNoncesTlv = null,
                                     CurrentCommitNonceTlv? currentCommitNonceTlv = null)
        : base(MessageTypes.ChannelReestablish, payload)
    {
        NextFundingTlv = nextFundingTlv;
        MyCurrentFundingLockedTlv = myCurrentFundingLockedTlv;
        NextLocalNoncesTlv = nextLocalNoncesTlv;
        CurrentCommitNonceTlv = currentCommitNonceTlv;

        if (NextFundingTlv is null && MyCurrentFundingLockedTlv is null && NextLocalNoncesTlv is null
         && CurrentCommitNonceTlv is null)
            return;

        // BOLT 1: ascending type order (1, 5, 22, 24)
        Extension = new TlvStream();
        Extension.Add(NextFundingTlv, MyCurrentFundingLockedTlv, NextLocalNoncesTlv, CurrentCommitNonceTlv);
    }
}