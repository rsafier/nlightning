namespace NLightning.Domain.Protocol.Onion.Interfaces;

using Constants;
using Crypto.ValueObjects;
using Enums;
using Exceptions;
using Models;
using ValueObjects;

/// <summary>
/// BOLT 4 Sphinx onion construction and peeling.
/// </summary>
/// <remarks>
/// <para>
/// The core works on any hop_payloads length (the peel stream is twice that length). Pass
/// <see cref="OnionPacketKind.OnionMessage"/> for onion messages (1300 or 32768 bytes, empty associated data), which
/// allows payloads shorter than 2 bytes; payment onions use <see cref="OnionPacketKind.Payment"/> (1300 bytes).
/// </para>
/// <para>
/// <c>pathKey</c> is only the route-blinding <c>path_key</c> received <b>alongside</b> the onion: TLV 0 of
/// <c>update_add_htlc</c>, or the <c>path_key</c> field of <c>onion_message</c>. It is never the payload's
/// <c>current_path_key</c>: an introduction point's onion is encrypted to its real node id, and
/// <c>current_path_key</c> is only used afterwards to decrypt <c>encrypted_recipient_data</c> and derive the next
/// path_key.
/// </para>
/// </remarks>
public interface ISphinxService
{
    /// <summary>
    /// Builds an onion packet for the given route.
    /// </summary>
    /// <param name="hops">The route, first hop first. Each payload excludes its bigsize length prefix.</param>
    /// <param name="sessionKey">A fresh random session key; it must never be reused.</param>
    /// <param name="associatedData">The associated data (the payment_hash for payments, empty for onion messages).</param>
    /// <param name="hopPayloadsLength">The hop_payloads length (1300 for payments).</param>
    /// <param name="packetKind">The packet kind; it sets the minimum payload length (2 for payments, 0 otherwise).</param>
    /// <returns>The onion packet to send to the first hop.</returns>
    /// <exception cref="ArgumentException">
    /// If the route is empty, a payload is too short for <paramref name="packetKind"/>, the payloads do not fit, or a
    /// key is invalid.
    /// </exception>
    OnionPacket Construct(IReadOnlyList<OnionHop> hops, PrivKey sessionKey, ReadOnlySpan<byte> associatedData,
                          int hopPayloadsLength = OnionConstants.HopPayloadsLength,
                          OnionPacketKind packetKind = OnionPacketKind.Payment);

    /// <summary>
    /// Builds an onion packet for the given route and returns the per-hop shared secrets along with it, so the origin
    /// does not have to redo the ECDH chain (<see cref="ComputeSharedSecrets"/>) to decrypt failures.
    /// </summary>
    /// <inheritdoc cref="Construct" path="/param"/>
    /// <inheritdoc cref="Construct" path="/exception"/>
    /// <returns>The packet and one 32-byte shared secret per hop, first hop first.</returns>
    ConstructedOnion ConstructWithSharedSecrets(IReadOnlyList<OnionHop> hops, PrivKey sessionKey,
                                                ReadOnlySpan<byte> associatedData,
                                                int hopPayloadsLength = OnionConstants.HopPayloadsLength,
                                                OnionPacketKind packetKind = OnionPacketKind.Payment);

    /// <summary>
    /// Computes the shared secret the sender shares with each hop for the given session key.
    /// </summary>
    /// <remarks>
    /// The origin keeps these to decrypt failure onions. Prefer <see cref="ConstructWithSharedSecrets"/> when building
    /// the onion; use this only to rebuild the secrets later (e.g. after a restart).
    /// </remarks>
    /// <param name="nodeIds">The hop public keys, first hop first.</param>
    /// <param name="sessionKey">The session key used to construct the onion.</param>
    /// <returns>One 32-byte shared secret per hop.</returns>
    /// <exception cref="ArgumentException">If the route is empty or a key is invalid.</exception>
    IReadOnlyList<Secret> ComputeSharedSecrets(IReadOnlyList<CompactPubKey> nodeIds, PrivKey sessionKey);

    /// <summary>
    /// Peels one layer of an onion packet as the local node, using the node key held by the key manager.
    /// </summary>
    /// <remarks>
    /// Named differently from <see cref="Peel"/> on purpose: a <c>byte[]</c> converts implicitly to both
    /// <see cref="PrivKey"/> and <see cref="CompactPubKey"/>, so overloads would silently bind a raw key to the wrong
    /// parameter.
    /// </remarks>
    /// <param name="packet">The received packet. It is never modified.</param>
    /// <param name="associatedData">The associated data (the payment_hash for payments, empty for onion messages).</param>
    /// <param name="pathKey">
    /// The path_key received alongside the onion (<c>update_add_htlc</c> TLV 0 or the <c>onion_message</c> path_key),
    /// if any. Never the payload's <c>current_path_key</c>.
    /// </param>
    /// <param name="packetKind">The packet kind (see <see cref="Peel"/>).</param>
    /// <exception cref="OnionException">See <see cref="Peel"/>.</exception>
    /// <exception cref="InvalidOperationException">If the implementation has no access to the node key.</exception>
    PeeledOnion PeelAsLocalNode(OnionPacket packet, ReadOnlySpan<byte> associatedData, CompactPubKey? pathKey = null,
                                OnionPacketKind packetKind = OnionPacketKind.Payment);

    /// <summary>
    /// Peels one layer of an onion packet using the given private key.
    /// </summary>
    /// <param name="packet">The received packet. It is never modified.</param>
    /// <param name="associatedData">The associated data (the payment_hash for payments, empty for onion messages).</param>
    /// <param name="nodeKey">The private key of the processing node.</param>
    /// <param name="pathKey">
    /// The path_key received alongside the onion (<c>update_add_htlc</c> TLV 0 or the <c>onion_message</c> path_key),
    /// if any. Never the payload's <c>current_path_key</c>.
    /// </param>
    /// <param name="packetKind">
    /// The packet kind: the minimum payload length is 2 for payments and 0 for onion messages.
    /// </param>
    /// <exception cref="OnionException">
    /// <para>
    /// Without a path_key (or for onion messages): <c>invalid_onion_version</c>, <c>invalid_onion_key</c>,
    /// <c>invalid_onion_blinding</c> (bad path_key) or <c>invalid_onion_hmac</c>, all carrying sha256_of_onion; or
    /// <c>invalid_onion_payload</c> (type 0, offset 0) for bad framing after the HMAC verified, with
    /// <see cref="OnionException.SharedSecret"/> set so the caller can encrypt it in <c>update_fail_htlc</c>.
    /// </para>
    /// <para>
    /// For a payment with a path_key (inside a blinded route), BOLT 4 requires every failure to be reported as
    /// <c>invalid_onion_blinding</c> with sha256_of_onion, so that is the only code thrown; the original failure is
    /// the inner exception.
    /// </para>
    /// </exception>
    PeeledOnion Peel(OnionPacket packet, ReadOnlySpan<byte> associatedData, PrivKey nodeKey,
                     CompactPubKey? pathKey = null, OnionPacketKind packetKind = OnionPacketKind.Payment);
}