namespace NLightning.Domain.Client.Requests;

using Channels.ValueObjects;

/// <summary>
/// Fails a channel and broadcasts our latest commitment (<c>ClientCommand.ForceCloseChannel</c>, BOLT 5 plan O2-T5).
/// </summary>
public sealed class ForceCloseChannelClientRequest
{
    public ChannelId ChannelId { get; }

    public ForceCloseChannelClientRequest(ChannelId channelId)
    {
        ChannelId = channelId;
    }
}