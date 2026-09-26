namespace NLightning.Domain.Gossip.Persistence;

using Crypto.ValueObjects;

/// <summary>
/// A node whose gossip we ignore until <paramref name="Until"/> (BOLT 7 plan §3.8 misbehaviour ban, table
/// <c>GraphBannedNodes</c>, primary key the node id).
/// </summary>
/// <param name="NodeId">The banned node.</param>
/// <param name="Reason">Why (for logs and IPC).</param>
/// <param name="Until">When the ban ends.</param>
public sealed record GraphBannedNodeRecord(CompactPubKey NodeId, string Reason, DateTimeOffset Until);