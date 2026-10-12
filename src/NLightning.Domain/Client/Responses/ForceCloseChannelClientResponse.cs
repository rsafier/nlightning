namespace NLightning.Domain.Client.Responses;

using Bitcoin.ValueObjects;
using Channels.Enums;
using Channels.ValueObjects;

/// <summary>
/// What a force close did (<c>ClientCommand.ForceCloseChannel</c>): the channel's state afterwards, how the broadcast
/// went (the failure service's status name: <c>Broadcast</c>, <c>Rebroadcast</c>, <c>PublishFailed</c>, ...) and our
/// commitment's txid, when one was signed.
/// </summary>
public sealed class ForceCloseChannelClientResponse
{
    public ChannelId ChannelId { get; }
    public ChannelState State { get; }
    public string Status { get; }
    public TxId? CommitmentTxId { get; }

    public ForceCloseChannelClientResponse(ChannelId channelId, ChannelState state, string status,
                                           TxId? commitmentTxId)
    {
        ChannelId = channelId;
        State = state;
        Status = status;
        CommitmentTxId = commitmentTxId;
    }
}