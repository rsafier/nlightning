using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Crypto.ValueObjects;

/// <summary>
/// Request for ListGraphChannels (ClientCommand 18, BOLT 7 plan G2-T6). Keys are append-only.
/// </summary>
[MessagePackObject]
public sealed class ListGraphChannelsIpcRequest
{
    /// <summary>Only this channel (the 8-byte short channel id as a number), when set.</summary>
    [Key(0)] public ulong? ShortChannelId { get; init; }

    /// <summary>Only the channels of this node, when set.</summary>
    [Key(1)] public CompactPubKey? NodeId { get; init; }

    public ListGraphChannelsClientRequest ToClientRequest() =>
        new()
        {
            ShortChannelId = ShortChannelId is { } scid ? new ShortChannelId(scid) : (ShortChannelId?)null,
            NodeId = NodeId
        };
}