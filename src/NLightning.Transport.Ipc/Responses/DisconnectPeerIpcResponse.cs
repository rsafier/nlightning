using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;

/// <summary>
/// Response for DisconnectPeer (ClientCommand 24).
/// </summary>
[MessagePackObject]
public sealed class DisconnectPeerIpcResponse
{
    [Key(0)] public required CompactPubKey NodeId { get; init; }

    /// <summary>The peer's channels that are not closed.</summary>
    [Key(1)] public int ChannelCount { get; init; }

    /// <summary>The HTLCs in flight on those channels (non-zero only for a forced disconnect).</summary>
    [Key(2)] public int HtlcsInFlight { get; init; }

    public static DisconnectPeerIpcResponse FromClientResponse(DisconnectPeerClientResponse clientResponse)
    {
        ArgumentNullException.ThrowIfNull(clientResponse);
        return new DisconnectPeerIpcResponse
        {
            NodeId = clientResponse.NodeId,
            ChannelCount = clientResponse.ChannelCount,
            HtlcsInFlight = clientResponse.HtlcsInFlight
        };
    }
}