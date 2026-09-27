using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Client.Requests;
using Domain.Crypto.ValueObjects;

/// <summary>
/// Request for ListPeerStorage (ClientCommand 32).
/// </summary>
[MessagePackObject]
public sealed class ListPeerStorageIpcRequest
{
    /// <summary>Only this peer; null for every peer.</summary>
    [Key(0)] public CompactPubKey? PeerNodeId { get; init; }

    /// <summary>Also return each retrieved blob's bytes.</summary>
    [Key(1)] public bool IncludeBlob { get; init; }

    public ListPeerStorageClientRequest ToClientRequest() => new(PeerNodeId, IncludeBlob);
}