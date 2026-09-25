namespace NLightning.Domain.Protocol.Onion.Interfaces;

using Constants;
using Crypto.ValueObjects;
using Exceptions;
using Models;
using ValueObjects;

/// <summary>
/// BOLT 4 Sphinx onion construction and peeling.
/// </summary>
/// <remarks>
/// The core works on any hop_payloads length (the peel stream is twice that length), so the same service serves
/// payment onions (1300 bytes) and onion messages (1300 or 32768 bytes).
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
    /// <returns>The onion packet to send to the first hop.</returns>
    /// <exception cref="ArgumentException">
    /// If the route is empty, a payload is shorter than 2 bytes, the payloads do not fit, or a key is invalid.
    /// </exception>
    OnionPacket Construct(IReadOnlyList<OnionHop> hops, PrivKey sessionKey, ReadOnlySpan<byte> associatedData,
                          int hopPayloadsLength = OnionConstants.HopPayloadsLength);

    /// <summary>
    /// Computes the shared secret the sender shares with each hop for the given session key.
    /// </summary>
    /// <remarks>The origin keeps these to decrypt failure onions.</remarks>
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
    /// <param name="pathKey">The route-blinding path_key, if any (update_add_htlc TLV or onion current_path_key).</param>
    /// <exception cref="OnionException">
    /// With <c>invalid_onion_version</c>, <c>invalid_onion_key</c>, <c>invalid_onion_blinding</c> or
    /// <c>invalid_onion_hmac</c> (all carrying sha256_of_onion), or <c>invalid_onion_payload</c>.
    /// </exception>
    /// <exception cref="InvalidOperationException">If the implementation has no access to the node key.</exception>
    PeeledOnion PeelAsLocalNode(OnionPacket packet, ReadOnlySpan<byte> associatedData, CompactPubKey? pathKey = null);

    /// <summary>
    /// Peels one layer of an onion packet using the given private key.
    /// </summary>
    /// <param name="packet">The received packet. It is never modified.</param>
    /// <param name="associatedData">The associated data (the payment_hash for payments, empty for onion messages).</param>
    /// <param name="nodeKey">The private key of the processing node.</param>
    /// <param name="pathKey">The route-blinding path_key, if any (update_add_htlc TLV or onion current_path_key).</param>
    /// <exception cref="OnionException">
    /// With <c>invalid_onion_version</c>, <c>invalid_onion_key</c>, <c>invalid_onion_blinding</c> or
    /// <c>invalid_onion_hmac</c> (all carrying sha256_of_onion), or <c>invalid_onion_payload</c>.
    /// </exception>
    PeeledOnion Peel(OnionPacket packet, ReadOnlySpan<byte> associatedData, PrivKey nodeKey,
                     CompactPubKey? pathKey = null);
}