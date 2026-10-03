using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Client.Requests;
using Domain.Crypto.ValueObjects;

/// <summary>
/// Request for DisconnectPeer (ClientCommand 24).
/// </summary>
[MessagePackObject]
public sealed class DisconnectPeerIpcRequest
{
    [Key(0)] public required CompactPubKey NodeId { get; init; }

    /// <summary>Disconnect even with HTLCs in flight.</summary>
    [Key(1)] public bool Force { get; init; }

    public DisconnectPeerClientRequest ToClientRequest() => new(NodeId) { Force = Force };
}