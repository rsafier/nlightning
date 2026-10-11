using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Client.Responses;

/// <summary>
/// Response for the ListChannels command.
/// </summary>
[MessagePackObject]
public sealed class ListChannelsIpcResponse
{
    [Key(0)] public required List<ChannelInfoIpcResponse> Channels { get; init; }

    public static ListChannelsIpcResponse FromClientResponse(ListChannelsClientResponse clientResponse)
    {
        return new ListChannelsIpcResponse
        {
            Channels = clientResponse.Channels.Select(ChannelInfoIpcResponse.FromClientResponse).ToList()
        };
    }
}