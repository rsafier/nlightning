using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Responses;

/// <summary>
/// Response for CloseChannel (ClientCommand 13): the channel's state and, once there is one, the closing transaction
/// id (hex, the usual display order).
/// </summary>
[MessagePackObject]
public sealed class CloseChannelIpcResponse
{
    [Key(0)] public required ChannelId ChannelId { get; init; }
    [Key(1)] public required ChannelState State { get; init; }
    [Key(2)] public string? ClosingTxId { get; init; }

    public static CloseChannelIpcResponse FromClientResponse(CloseChannelClientResponse clientResponse)
    {
        ArgumentNullException.ThrowIfNull(clientResponse);
        return new CloseChannelIpcResponse
        {
            ChannelId = clientResponse.ChannelId,
            State = clientResponse.State,
            ClosingTxId = clientResponse.ClosingTxId is { } txId
                              ? Convert.ToHexString(((byte[])txId).Reverse().ToArray()).ToLowerInvariant()
                              : null
        };
    }
}