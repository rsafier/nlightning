namespace NLightning.Domain.Protocol.Onion.Interfaces;

using Constants;
using Crypto.ValueObjects;
using Exceptions;
using Models;

/// <summary>
/// BOLT 4 trampoline onion (BOLTs PR 836): the <c>trampoline_onion_packet</c> carried in TLV 20 of the outer onion's
/// final hop payload.
/// </summary>
/// <remarks>
/// <para>
/// The trampoline onion is built and peeled with the payment onion's Sphinx construction (<see cref="ISphinxService"/>:
/// <c>rho</c>/<c>mu</c>/<c>pad</c> keys, filler, version 0, payloads of at least 2 bytes), with a variable
/// <c>hop_payloads</c> length and the payment hash as associated data. Its session key MUST differ from the outer
/// onion's.
/// </para>
/// <para>
/// Hop payloads are raw TLV streams without their bigsize length prefix, as for <see cref="OnionHop"/>; parsing and
/// validating them (<c>outgoing_node_id</c>, <c>recipient_blinded_paths</c>, ...) belongs to the caller.
/// </para>
/// </remarks>
public interface ITrampolineOnionService
{
    /// <summary>
    /// Builds a trampoline onion for the given trampoline hops.
    /// </summary>
    /// <param name="hops">The trampoline hops (node ids or blinded node ids), first hop first.</param>
    /// <param name="sessionKey">A fresh random session key, never the outer onion's.</param>
    /// <param name="paymentHash">The payment hash (32 bytes), the associated data.</param>
    /// <param name="sizePolicy">How to size <c>hop_payloads</c>.</param>
    /// <returns>The packet and the shared secret with each trampoline hop.</returns>
    /// <exception cref="ArgumentException">
    /// If the route is empty, a payload is shorter than 2 bytes, the payloads do not fit under
    /// <paramref name="sizePolicy"/>, the payment hash is not 32 bytes or a key is invalid.
    /// </exception>
    TrampolineOnion Build(IReadOnlyList<OnionHop> hops, PrivKey sessionKey, ReadOnlySpan<byte> paymentHash,
                          TrampolineOnionSizePolicy sizePolicy);

    /// <summary>
    /// Peels one layer of a received trampoline onion as the local node (node-key ECDH in the key manager).
    /// </summary>
    /// <param name="trampolinePacket">The value of the outer payload's TLV 20.</param>
    /// <param name="paymentHash">The HTLC's payment hash (32 bytes).</param>
    /// <param name="pathKey">
    /// The route-blinding path key when this node is a blinded trampoline hop past the introduction point: the
    /// <c>current_path_key</c> of the <b>outer</b> payload. Never the trampoline payload's own
    /// <c>current_path_key</c> (an introduction node peels with its real node key and reads that one afterwards).
    /// </param>
    /// <returns>
    /// The peeled layer: <see cref="PeeledOnion.Payload"/> is this node's trampoline payload,
    /// <see cref="PeeledOnion.SharedSecret"/> the trampoline shared secret (to create or wrap trampoline failures),
    /// and <see cref="PeeledOnion.NextPacket"/> the packet for the next trampoline node (null when final), with the
    /// same <c>hop_payloads</c> length.
    /// </returns>
    /// <exception cref="OnionException">
    /// <para>
    /// A value too short to hold a packet: <c>invalid_onion_payload</c> naming TLV 20 at offset 0 (the caller knows the
    /// real offset of the record and may rebuild the data).
    /// </para>
    /// <para>
    /// Otherwise as <see cref="ISphinxService.PeelAsLocalNode"/>: a BADONION code (version, key, HMAC, or
    /// <c>invalid_onion_blinding</c> with a <paramref name="pathKey"/>) carries the sha256 of the <b>trampoline</b>
    /// packet. The outer onion was valid, so such a failure is not an <c>update_fail_malformed_htlc</c>: the caller
    /// encrypts it for the origin with the outer shared secret in an <c>update_fail_htlc</c>.
    /// </para>
    /// </exception>
    /// <exception cref="ArgumentException">If <paramref name="paymentHash"/> is not 32 bytes.</exception>
    /// <exception cref="InvalidOperationException">If no key manager is available.</exception>
    PeeledOnion Peel(ReadOnlyMemory<byte> trampolinePacket, ReadOnlySpan<byte> paymentHash,
                     CompactPubKey? pathKey = null);

    /// <summary>
    /// Peels one layer of a received trampoline onion with the given node key (tests, tools).
    /// </summary>
    /// <remarks>
    /// Named differently from <see cref="Peel"/> on purpose: a <c>byte[]</c> converts implicitly to both
    /// <see cref="PrivKey"/> and <see cref="CompactPubKey"/>.
    /// </remarks>
    /// <inheritdoc cref="Peel" path="/param"/>
    /// <inheritdoc cref="Peel" path="/returns"/>
    /// <inheritdoc cref="Peel" path="/exception"/>
    /// <param name="nodeKey">The private key of the processing node.</param>
    PeeledOnion PeelWithNodeKey(ReadOnlyMemory<byte> trampolinePacket, ReadOnlySpan<byte> paymentHash, PrivKey nodeKey,
                                CompactPubKey? pathKey = null);

    /// <summary>
    /// The framed length of one hop payload in a Sphinx onion: <c>bigsize(len) + len + 32</c>.
    /// </summary>
    /// <param name="payloadLength">The payload length without its bigsize prefix.</param>
    int GetFramedPayloadLength(int payloadLength);

    /// <summary>
    /// The largest trampoline <c>hop_payloads</c> length that still lets the outer onion fit, so a caller can pass
    /// it to <see cref="TrampolineOnionSizePolicy.Auto"/>.
    /// </summary>
    /// <param name="outerHopsFramedLength">
    /// The framed payloads of the outer hops <b>before</b> the final one (the sum of
    /// <see cref="GetFramedPayloadLength"/>), 0 when the first trampoline node is the outer route's only hop.
    /// </param>
    /// <param name="finalPayloadOtherTlvsLength">
    /// The length of the outer final payload's TLV records other than <c>trampoline_onion_packet</c>
    /// (<c>amt_to_forward</c>, <c>outgoing_cltv_value</c>, <c>payment_data</c>, ...).
    /// </param>
    /// <param name="outerHopPayloadsLength">The outer onion's <c>hop_payloads</c> length (1300 for payments).</param>
    /// <returns>The maximum length, or 0 when not even one byte of <c>hop_payloads</c> fits.</returns>
    /// <exception cref="ArgumentOutOfRangeException">If an argument is negative.</exception>
    int GetMaxHopPayloadsLength(int outerHopsFramedLength, int finalPayloadOtherTlvsLength,
                                int outerHopPayloadsLength = OnionConstants.HopPayloadsLength);
}