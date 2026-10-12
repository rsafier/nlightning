using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Client.Requests;
using Domain.Crypto.ValueObjects;

/// <summary>
/// Request for ListNodes (ClientCommand 17, BOLT 7 plan G2-T6). Keys are append-only.
/// </summary>
[MessagePackObject]
public sealed class ListNodesIpcRequest
{
    /// <summary>Only this node, when set.</summary>
    [Key(0)] public CompactPubKey? NodeId { get; init; }

    public ListNodesClientRequest ToClientRequest() => new() { NodeId = NodeId };
}