using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Client.Responses;

/// <summary>
/// Response for Shutdown (ClientCommand 39, NL-591): the node is stopping. A refusal (HTLCs in flight, a shutdown
/// already running) is an error envelope instead.
/// </summary>
[MessagePackObject]
public sealed class ShutdownIpcResponse
{
    /// <summary>The channels that are not closed; they reestablish when the node starts again.</summary>
    [Key(0)] public int ChannelCount { get; init; }

    public static ShutdownIpcResponse FromClientResponse(ShutdownClientResponse clientResponse)
    {
        ArgumentNullException.ThrowIfNull(clientResponse);
        return new ShutdownIpcResponse { ChannelCount = clientResponse.ChannelCount };
    }
}