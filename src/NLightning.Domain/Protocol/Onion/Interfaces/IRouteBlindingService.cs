namespace NLightning.Domain.Protocol.Onion.Interfaces;

using Crypto.ValueObjects;
using Exceptions;
using Models;

/// <summary>
/// BOLT 4 route blinding (ONION M5): building blinded paths and reading a hop's <c>encrypted_recipient_data</c>.
/// </summary>
/// <remarks>
/// <para>Writer (the creator of a blinded path, usually the recipient): <see cref="CreateBlindedPath"/> chains the
/// path keys <c>E_{i+1} = SHA256(E_i || ss_i) * E_i</c> from a fresh session key, blinds each node id
/// (<c>B_i = HMAC256("blinded_node_id", ss_i) * N_i</c>) and encrypts each hop's <c>encrypted_data_tlv</c> with
/// <c>rho_i = HMAC256("rho", ss_i)</c> (ChaCha20-Poly1305, all-zero nonce, no associated data).</para>
/// <para>Reader (a blinded hop): <see cref="UnblindAsLocalNode"/> decrypts with <c>ss_i = ECDH(E_i, node_key)</c>
/// (done inside the key manager) and derives the next path_key. Every failure (bad path_key, authentication failure,
/// malformed or unknown even records) is an <see cref="OnionException"/> with <c>invalid_onion_blinding</c>.</para>
/// <para>Stateless and thread-safe.</para>
/// </remarks>
public interface IRouteBlindingService
{
    /// <summary>
    /// Creates a blinded path over <paramref name="nodeIds"/> (introduction node first) with one plaintext
    /// <c>encrypted_data_tlv</c> per node.
    /// </summary>
    /// <param name="nodeIds">The real node ids, introduction node first, recipient last.</param>
    /// <param name="encryptedDataTlvs">The serialized <c>encrypted_data_tlv</c> of each node
    /// (<see cref="EncodeRecipientData"/>).</param>
    /// <param name="sessionKey">The path's first ephemeral private key <c>e_0</c> (fresh, from a CSPRNG).</param>
    /// <exception cref="ArgumentException">If the lists are empty or differ in length, or a key is invalid.</exception>
    BlindedPath CreateBlindedPath(IReadOnlyList<CompactPubKey> nodeIds, IReadOnlyList<byte[]> encryptedDataTlvs,
                                  PrivKey sessionKey);

    /// <summary>
    /// Decrypts the <c>encrypted_recipient_data</c> of this node with the node key held by the key manager.
    /// </summary>
    /// <param name="pathKey">The path_key <c>E_i</c>: the <c>update_add_htlc</c> path_key, or the payload's
    /// <c>current_path_key</c> at the introduction node.</param>
    /// <param name="encryptedRecipientData">The payload's <c>encrypted_recipient_data</c>.</param>
    /// <param name="pathKeySharedSecret">The already computed <c>ECDH(path_key, node_key)</c>
    /// (<see cref="PeeledOnion.PathKeySharedSecret"/>), if any, to skip redoing the ECDH.</param>
    /// <exception cref="OnionException">Always <c>invalid_onion_blinding</c>, on any failure.</exception>
    /// <exception cref="InvalidOperationException">If no key manager is available.</exception>
    BlindedHopUnblinding UnblindAsLocalNode(CompactPubKey pathKey, ReadOnlyMemory<byte> encryptedRecipientData,
                                            Secret? pathKeySharedSecret = null);

    /// <summary>
    /// Decrypts an <c>encrypted_recipient_data</c> with the given node key (tests and tools).
    /// </summary>
    /// <inheritdoc cref="UnblindAsLocalNode" path="/param"/>
    /// <exception cref="OnionException">Always <c>invalid_onion_blinding</c>, on any failure.</exception>
    BlindedHopUnblinding Unblind(PrivKey nodeKey, CompactPubKey pathKey, ReadOnlyMemory<byte> encryptedRecipientData);

    /// <summary>
    /// Decodes a plaintext <c>encrypted_data_tlv</c> stream.
    /// </summary>
    /// <exception cref="OnionException"><c>invalid_onion_blinding</c> if the stream is malformed (non-canonical,
    /// unordered, truncated, a wrong record length, a non-minimal truncated integer, an invalid point) or holds an
    /// unknown even record.</exception>
    BlindedRecipientData DecodeRecipientData(ReadOnlySpan<byte> encryptedDataTlv);

    /// <summary>
    /// Serializes an <c>encrypted_data_tlv</c> stream (records in ascending type order).
    /// </summary>
    byte[] EncodeRecipientData(BlindedRecipientData recipientData);
}