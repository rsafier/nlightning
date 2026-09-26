using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Channels.ValueObjects;
using Domain.Client.Requests;

/// <summary>
/// Request for ForceCloseChannel (ClientCommand 14).
/// </summary>
[MessagePackObject]
public sealed class ForceCloseChannelIpcRequest
{
    [Key(0)] public required ChannelId ChannelId { get; init; }

    public ForceCloseChannelClientRequest ToClientRequest() => new(ChannelId);
}