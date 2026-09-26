namespace NLightning.Domain.Protocol.Onion.Interfaces;

using Constants;
using Crypto.ValueObjects;
using Enums;
using Models;
using Protocol.Tlv;

/// <summary>
/// BOLT 4 attributable failures and hold times (<c>option_attribution_data</c>): builds, updates and verifies the
/// <c>attribution_data</c> of <c>update_fail_htlc</c> and <c>update_fulfill_htlc</c>, and the
/// <c>fulfillment_payload</c> of <c>update_fulfill_htlc</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>attribution_data</c> is <c>htlc_hold_times</c> (20 × u32, this node's first) followed by 210 truncated HMACs:
/// 20 per hop, one for each position the hop could hold in the route. Each HMAC uses the hop's <c>um</c> key and
/// covers the message ("the return packet before applying the pseudo-random byte stream", or for a fulfill the
/// <c>fulfillment_payload</c> as received from downstream), the first <c>y + 1</c> hold times and the <c>y</c>
/// downstream HMACs. A node shifts the downstream data right (pruning HMACs past position 20), adds its hold time and
/// HMACs, and XORs the whole block with its <c>ammagext</c> stream.
/// </para>
/// <para>
/// Hold times are in units of 100 ms (<see cref="AttributionHoldTime"/>). A node whose incoming
/// <c>update_add_htlc</c> carried a <c>path_key</c> MUST NOT add attribution data (it fails with
/// <c>update_fail_malformed_htlc</c>); it still obfuscates and relays a <c>fulfillment_payload</c>
/// (<see cref="WrapFulfillmentPayload"/>).
/// </para>
/// </remarks>
public interface IAttributionDataService
{
    /// <summary>
    /// Erring node: builds the return packet (as <see cref="IFailureOnionService.CreateErrorPacket"/>) and initializes
    /// its <c>attribution_data</c>.
    /// </summary>
    /// <param name="sharedSecret">The shared secret of the incoming onion.</param>
    /// <param name="message">The failure message.</param>
    /// <param name="holdTime">This node's hold time, in units of 100 ms.</param>
    /// <param name="minFailurePadLength">The minimum of <c>failure_len + pad_len</c> (at least 256).</param>
    AttributedErrorPacket CreateErrorPacket(Secret sharedSecret, FailureMessage message, uint holdTime,
                                            int minFailurePadLength = OnionConstants.MinFailurePadLength);

    /// <summary>
    /// Erring node for an <c>update_fail_malformed_htlc</c> received downstream: builds the return packet as
    /// <see cref="IFailureOnionService.CreateErrorPacketFromMalformed"/> and initializes its <c>attribution_data</c>.
    /// </summary>
    AttributedErrorPacket CreateErrorPacketFromMalformed(Secret incomingSharedSecret, FailureCode failureCode,
                                                         ReadOnlySpan<byte> sha256OfOnion, uint holdTime,
                                                         int minFailurePadLength =
                                                             OnionConstants.MinFailurePadLength);

    /// <summary>
    /// Intermediate node: obfuscates the return packet received from downstream (truncated to 32768 bytes first) and
    /// transforms and updates the downstream <c>attribution_data</c>.
    /// </summary>
    /// <param name="sharedSecret">The shared secret of the incoming onion this HTLC was forwarded for.</param>
    /// <param name="errorPacket">The downstream <c>update_fail_htlc.reason</c>.</param>
    /// <param name="downstreamAttributionData">
    /// The downstream <c>attribution_data</c>, or empty when none was received (an all-zero block is used, as BOLT 4
    /// requires).
    /// </param>
    /// <param name="holdTime">This node's hold time, in units of 100 ms.</param>
    AttributedErrorPacket WrapErrorPacket(Secret sharedSecret, ReadOnlySpan<byte> errorPacket,
                                          ReadOnlySpan<byte> downstreamAttributionData, uint holdTime);

    /// <summary>
    /// Origin node: decrypts the return packet (as <see cref="IFailureOnionService.DecryptErrorPacket"/>) and verifies
    /// the <c>attribution_data</c> hop by hop.
    /// </summary>
    /// <param name="hopSharedSecrets">The shared secret of each hop of the route, first hop first.</param>
    /// <param name="errorPacket">The <c>update_fail_htlc.reason</c>.</param>
    /// <param name="attributionData">The <c>update_fail_htlc</c> <c>attribution_data</c>, or empty when none.</param>
    AttributedFailure DecryptErrorPacket(IReadOnlyList<Secret> hopSharedSecrets, ReadOnlySpan<byte> errorPacket,
                                         ReadOnlySpan<byte> attributionData);

    /// <summary>
    /// Final node: initializes the <c>attribution_data</c> of its <c>update_fulfill_htlc</c> and, when
    /// <paramref name="fulfillmentRecords"/> is not <c>null</c>, builds a <c>fulfillment_payload</c>: the records plus
    /// <c>padding</c> (to a multiple of 256 bytes, 256 when possible), encrypted with ChaCha20-Poly1305 under the
    /// <c>fulfillment</c> key and an all-zero nonce. Its HMACs do not cover the payload.
    /// </summary>
    /// <param name="sharedSecret">The shared secret of the incoming onion.</param>
    /// <param name="holdTime">This node's hold time, in units of 100 ms.</param>
    /// <param name="fulfillmentRecords">
    /// The <c>fulfillment_payload_tlvs</c> records to send (raw values; distinct types, none of them <c>padding</c>),
    /// or <c>null</c> for no payload.
    /// </param>
    /// <exception cref="ArgumentException">
    /// If a record is <c>padding</c> or repeated, or the payload would exceed 32768 bytes.
    /// </exception>
    AttributedFulfillment CreateFulfillment(Secret sharedSecret, uint holdTime,
                                            IReadOnlyList<BaseTlv>? fulfillmentRecords = null);

    /// <summary>
    /// Intermediate node: transforms and updates the downstream <c>attribution_data</c> of an
    /// <c>update_fulfill_htlc</c>, covering the downstream <c>fulfillment_payload</c> as received, then obfuscates that
    /// payload with this node's <c>ammag</c> key.
    /// </summary>
    /// <param name="sharedSecret">The shared secret of the incoming onion this HTLC was forwarded for.</param>
    /// <param name="downstreamAttributionData">The downstream <c>attribution_data</c>, or empty when none.</param>
    /// <param name="downstreamFulfillmentPayload">The downstream <c>fulfillment_payload</c>, or empty when none.</param>
    /// <param name="holdTime">This node's hold time, in units of 100 ms.</param>
    /// <exception cref="ArgumentException">If the payload is longer than 32768 bytes.</exception>
    AttributedFulfillment WrapFulfillment(Secret sharedSecret, ReadOnlySpan<byte> downstreamAttributionData,
                                          ReadOnlySpan<byte> downstreamFulfillmentPayload, uint holdTime);

    /// <summary>
    /// Obfuscates a downstream <c>fulfillment_payload</c> with this node's <c>ammag</c> key, without touching
    /// <c>attribution_data</c> (a hop reached through a blinded path).
    /// </summary>
    /// <exception cref="ArgumentException">If the payload is longer than 32768 bytes.</exception>
    byte[] WrapFulfillmentPayload(Secret sharedSecret, ReadOnlySpan<byte> fulfillmentPayload);

    /// <summary>
    /// Origin node: verifies the <c>attribution_data</c> of an <c>update_fulfill_htlc</c> (the final node's HMACs
    /// without the payload, each intermediate hop's over the payload it received) and recovers the
    /// <c>fulfillment_payload</c> records.
    /// </summary>
    /// <param name="hopSharedSecrets">The shared secret of each hop of the route, first hop first.</param>
    /// <param name="attributionData">The <c>attribution_data</c>, or empty when none.</param>
    /// <param name="fulfillmentPayload">The <c>fulfillment_payload</c>, or empty when none.</param>
    VerifiedFulfillment VerifyFulfillment(IReadOnlyList<Secret> hopSharedSecrets, ReadOnlySpan<byte> attributionData,
                                          ReadOnlySpan<byte> fulfillmentPayload);
}