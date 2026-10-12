namespace NLightning.Domain.Protocol.OnionMessages.Interfaces;

using Crypto.ValueObjects;
using Messages;
using Onion.Models;

/// <summary>
/// Builds the <c>onion_message</c> for the first hop (BOLT 4 "Onion Messages", writer): Sphinx with empty associated
/// data over the unblinded prefix hops and the blinded path.
/// </summary>
/// <remarks>
/// Each prefix hop carries only <c>encrypted_recipient_data</c>, built by the sender (a blinded path over the prefix
/// whose last hop has <c>next_node_id</c> = the introduction node and <c>next_path_key_override</c> =
/// <c>first_path_key</c>). The final hop carries its <c>encrypted_recipient_data</c> from the path, the
/// <paramref name="replyPath"/> and the contents. The payloads are 1300 bytes when they fit, else 32768.
/// </remarks>
public interface IOnionMessagePacketBuilder
{
    /// <summary>
    /// Builds the message to send to the first prefix hop, or to the introduction node when there is no prefix.
    /// </summary>
    /// <param name="prefixNodeIds">The real node ids from the first hop up to, not including, the introduction node
    /// (empty when the introduction node is our peer).</param>
    /// <param name="path">The destination's blinded path, its introduction node resolved (for a node-id destination,
    /// a path the sender created to it).</param>
    /// <param name="contents">The final hop's records.</param>
    /// <param name="replyPath">The final hop's <c>reply_path</c>, or null.</param>
    /// <param name="sessionKey">The Sphinx session key; null draws a fresh one (tests pass the vector's).</param>
    /// <param name="prefixPathSessionKey">The session key of the prefix's blinding; null draws a fresh one.</param>
    /// <returns>The <c>onion_message</c> with its <c>path_key</c> and packet.</returns>
    /// <exception cref="ArgumentException">The hops do not fit 32768 bytes of payloads, or a key or the path is
    /// invalid.</exception>
    OnionMessageMessage Build(IReadOnlyList<CompactPubKey> prefixNodeIds, BlindedPath path,
                              OnionMessageContents contents, WireBlindedPath? replyPath, PrivKey? sessionKey = null,
                              PrivKey? prefixPathSessionKey = null);
}