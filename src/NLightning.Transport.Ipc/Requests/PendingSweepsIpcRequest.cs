using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Channels.ValueObjects;
using Domain.Client.Requests;

/// <summary>
/// Request for PendingSweeps (ClientCommand 15).
/// </summary>
[MessagePackObject]
public sealed class PendingSweepsIpcRequest
{
    /// <summary>Only this channel, when set.</summary>
    [Key(0)] public ChannelId? ChannelId { get; init; }

    /// <summary>Also the channels already closed.</summary>
    [Key(1)] public bool IncludeClosed { get; init; }

    public PendingSweepsClientRequest ToClientRequest() =>
        new() { ChannelId = ChannelId, IncludeClosed = IncludeClosed };
}