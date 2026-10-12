namespace NLightning.Domain.Protocol.OnionMessages;

using Crypto.ValueObjects;
using Onion.Models;

/// <summary>
/// A BOLT 4 <c>blinded_path</c> as it appears on the wire (<c>reply_path</c>, and BOLT 12 <c>offer_paths</c>,
/// <c>invreq_paths</c> and <c>invoice_paths</c>): its introduction node is a <see cref="SciddirOrPubkey"/>, which may be
/// an unresolved SCID and direction.
/// </summary>
/// <remarks>
/// M5's <see cref="BlindedPath"/> needs the introduction node's id; use it once <see cref="FirstNode"/> is resolved.
/// </remarks>
/// <param name="FirstNode">The introduction node (<c>first_node_id</c>).</param>
/// <param name="FirstPathKey">The first path_key <c>E_0</c> (<c>first_path_key</c>).</param>
/// <param name="Hops">The blinded hops, introduction node first and recipient last (at least one on the wire).</param>
public sealed record WireBlindedPath(SciddirOrPubkey FirstNode, CompactPubKey FirstPathKey,
                                     IReadOnlyList<BlindedPathHop> Hops)
{
    /// <summary>
    /// The wire form of a path whose introduction node id is known.
    /// </summary>
    public static WireBlindedPath FromBlindedPath(BlindedPath path) =>
        new(SciddirOrPubkey.FromNodeId(path.FirstNodeId), path.FirstPathKey, path.Hops);
}