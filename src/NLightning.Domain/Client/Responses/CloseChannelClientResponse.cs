namespace NLightning.Domain.Client.Responses;

using Bitcoin.ValueObjects;
using Channels.Enums;
using Channels.ValueObjects;

/// <summary>
/// Where the cooperative close of a channel stands (<c>ClientCommand.CloseChannel</c>): its state and, once agreed and
/// broadcast, the closing transaction id.
/// </summary>
public sealed class CloseChannelClientResponse
{
    public ChannelId ChannelId { get; }
    public ChannelState State { get; }
    public TxId? ClosingTxId { get; }

    public CloseChannelClientResponse(ChannelId channelId, ChannelState state, TxId? closingTxId)
    {
        ChannelId = channelId;
        State = state;
        ClosingTxId = closingTxId;
    }
}