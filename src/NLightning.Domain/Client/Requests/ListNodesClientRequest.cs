namespace NLightning.Domain.Client.Requests;

using Crypto.ValueObjects;

/// <summary>
/// Lists the announced nodes of the gossip graph (<c>ClientCommand.ListNodes</c>, BOLT 7 plan G2-T6).
/// </summary>
public sealed class ListNodesClientRequest
{
    /// <summary>Only this node, when set.</summary>
    public CompactPubKey? NodeId { get; init; }
}