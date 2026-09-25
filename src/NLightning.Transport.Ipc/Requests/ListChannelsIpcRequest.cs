using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Client.Requests;
using Domain.Crypto.ValueObjects;

/// <summary>
/// Request for ListChannels.
/// </summary>
[MessagePackObject]
public sealed class ListChannelsIpcRequest
{
    /// <summary>
    /// When set, only the channels with this peer are listed.
    /// </summary>
    [Key(0)] public CompactPubKey? PeerId { get; init; }

    public ListChannelsClientRequest ToClientRequest()
    {
        return new ListChannelsClientRequest { PeerId = PeerId };
    }
}