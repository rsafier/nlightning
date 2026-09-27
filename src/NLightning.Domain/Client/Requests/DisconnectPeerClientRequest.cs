namespace NLightning.Domain.Client.Requests;

using Crypto.ValueObjects;

/// <summary>
/// Disconnects a connected peer (<c>ClientCommand.DisconnectPeer</c>, NL-152).
/// </summary>
/// <remarks>
/// Refused while a channel with the peer has HTLCs in flight, unless <see cref="Force"/> is set. A peer disconnected
/// this way is not reconnected by the node until the next <c>connect</c> or restart; the peer may still connect to us.
/// </remarks>
public sealed class DisconnectPeerClientRequest
{
    public DisconnectPeerClientRequest(CompactPubKey nodeId)
    {
        NodeId = nodeId;
    }

    /// <summary>The peer to disconnect.</summary>
    public CompactPubKey NodeId { get; }

    /// <summary>Disconnect even with HTLCs in flight.</summary>
    public bool Force { get; init; }
}