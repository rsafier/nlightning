namespace NLightning.Domain.Protocol.OnionMessages.Interfaces;

using Crypto.ValueObjects;
using Onion.Models;

/// <summary>
/// Creates blinded paths for onion messages (BOLT 4 "Onion Messages", creator of <c>encrypted_recipient_data</c>):
/// our reply paths, the path a sender builds to a node id, and the sender's prefix up to an introduction node.
/// </summary>
/// <remarks>
/// <para>Every hop's <c>encrypted_data_tlv</c> is padded to the same length (BOLT 4 route blinding: the creator
/// SHOULD make all hops equal in size) with a <c>padding</c> record, which the builder owns: any padding given by the
/// caller is replaced. The longest hop gets no padding record unless the target had to grow (a difference
/// of 1, 255 or 256 bytes cannot be filled exactly), in which case every hop gets one.</para>
/// <para>Writer rules enforced: no <c>payment_relay</c> or <c>payment_constraints</c> anywhere, every non-final hop
/// names its next hop (<c>next_node_id</c> or <c>short_channel_id</c>) and carries no <c>path_id</c>.</para>
/// <para>The node's one message-path builder (NL-442): the packet builder's prefix, the service's paths to a node id
/// and its reply paths, and the offer paths. Implemented by <c>Infrastructure.Bitcoin/Onion/OnionMessages/
/// BlindedMessagePathBuilder</c> (byte-exact against <c>blinded-onion-message-onion-test.json</c>).</para>
/// <para>Stateless and thread-safe.</para>
/// </remarks>
public interface IBlindedMessagePathBuilder
{
    /// <summary>
    /// Creates a blinded path over <paramref name="nodeIds"/> with the given plaintext data per hop.
    /// </summary>
    /// <param name="nodeIds">The real node ids, introduction node first, recipient last.</param>
    /// <param name="recipientData">One <c>encrypted_data_tlv</c> per hop, without padding.</param>
    /// <param name="sessionKey">The path's first ephemeral private key; null draws a fresh one.</param>
    /// <exception cref="ArgumentException">The lists are empty or differ in length, a writer rule is broken, or a key
    /// is invalid.</exception>
    BlindedPath CreatePath(IReadOnlyList<CompactPubKey> nodeIds, IReadOnlyList<BlindedRecipientData> recipientData,
                           PrivKey? sessionKey = null);

    /// <summary>
    /// Creates a message path to the last of <paramref name="nodeIds"/>: each non-final hop gets
    /// <c>next_node_id</c> = the next node, and the final hop gets <paramref name="pathId"/> (when given).
    /// </summary>
    /// <param name="nodeIds">The real node ids, introduction node first, recipient last.</param>
    /// <param name="pathId">The recipient's <c>path_id</c> (a secret of ours for a reply path), or null.</param>
    /// <param name="sessionKey">The path's first ephemeral private key; null draws a fresh one.</param>
    /// <exception cref="ArgumentException">The list is empty or a key is invalid.</exception>
    BlindedPath CreateMessagePath(IReadOnlyList<CompactPubKey> nodeIds, ReadOnlyMemory<byte>? pathId = null,
                                  PrivKey? sessionKey = null);
}