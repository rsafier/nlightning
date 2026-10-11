namespace NLightning.Domain.Protocol.Onion.Models;

using Crypto.ValueObjects;

/// <summary>
/// A BOLT 4 <c>blinded_path</c>: an introduction node, the first path_key and the blinded hops, introduction node
/// first and recipient last.
/// </summary>
/// <param name="FirstNodeId">The introduction node's real node id (<c>first_node_id</c>).</param>
/// <param name="FirstPathKey">The first path_key <c>E_0</c> (<c>first_path_key</c>).</param>
/// <param name="Hops">The blinded hops.</param>
public sealed record BlindedPath(CompactPubKey FirstNodeId, CompactPubKey FirstPathKey,
                                 IReadOnlyList<BlindedPathHop> Hops);