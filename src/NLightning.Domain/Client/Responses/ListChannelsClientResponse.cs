namespace NLightning.Domain.Client.Responses;

/// <summary>
/// The node's channels, the ones loaded in memory first, then the persisted ones that are not (closed or stale).
/// </summary>
public sealed class ListChannelsClientResponse
{
    public IReadOnlyList<ChannelInfoClientResponse> Channels { get; }

    public ListChannelsClientResponse(IReadOnlyList<ChannelInfoClientResponse> channels)
    {
        Channels = channels;
    }
}