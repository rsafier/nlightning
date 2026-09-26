namespace NLightning.Domain.Channels.Commitments;

using Crypto.ValueObjects;

/// <summary>
/// How an HTLC was removed, with the data of the removing message.
/// </summary>
/// <param name="Kind">Fulfill, fail or fail-malformed.</param>
/// <param name="PaymentPreimage">The preimage (fulfill only).</param>
/// <param name="Reason">The opaque failure onion (fail only).</param>
/// <param name="FailureCode">The BOLT 4 failure code (fail-malformed only).</param>
/// <param name="Sha256OfOnion">The onion hash (fail-malformed only).</param>
/// <param name="AttributionData">The <c>attribution_data</c> TLV of the <c>update_fulfill_htlc</c> or
/// <c>update_fail_htlc</c> (BOLT 4 attributable failures and hold times, 920 bytes), empty when the message carried
/// none. Opaque to the engine; kept so the removal can be retransmitted as sent and relayed or verified after a
/// restart (NL-326).</param>
/// <param name="FulfillmentPayload">The <c>fulfillment_payload</c> TLV of the <c>update_fulfill_htlc</c> (fulfill
/// only), empty when none.</param>
public sealed record HtlcRemoval(
    HtlcRemovalKind Kind,
    Secret? PaymentPreimage = null,
    ReadOnlyMemory<byte> Reason = default,
    ushort FailureCode = 0,
    ReadOnlyMemory<byte> Sha256OfOnion = default,
    ReadOnlyMemory<byte> AttributionData = default,
    ReadOnlyMemory<byte> FulfillmentPayload = default)
{
    /// <param name="paymentPreimage">The preimage.</param>
    /// <param name="attributionData">The fulfill's <c>attribution_data</c>, or empty.</param>
    /// <param name="fulfillmentPayload">The fulfill's <c>fulfillment_payload</c>, or empty.</param>
    public static HtlcRemoval Fulfill(Secret paymentPreimage, ReadOnlyMemory<byte> attributionData = default,
                                      ReadOnlyMemory<byte> fulfillmentPayload = default) =>
        new(HtlcRemovalKind.Fulfill, paymentPreimage, AttributionData: attributionData,
            FulfillmentPayload: fulfillmentPayload);

    /// <param name="reason">The opaque failure onion.</param>
    /// <param name="attributionData">The failure's <c>attribution_data</c>, or empty.</param>
    public static HtlcRemoval Fail(ReadOnlyMemory<byte> reason, ReadOnlyMemory<byte> attributionData = default) =>
        new(HtlcRemovalKind.Fail, Reason: reason, AttributionData: attributionData);

    public static HtlcRemoval FailMalformed(ushort failureCode, ReadOnlyMemory<byte> sha256OfOnion) =>
        new(HtlcRemovalKind.FailMalformed, FailureCode: failureCode, Sha256OfOnion: sha256OfOnion);

    /// <summary>An HTLC we offered that was settled on chain without a preimage (BOLT 5 plan O3-T4).</summary>
    public static HtlcRemoval OnchainTimeout() => new(HtlcRemovalKind.OnchainTimeout);

    /// <summary>True for a fulfill: the amount goes to the receiver of the HTLC.</summary>
    public bool IsFulfill => Kind == HtlcRemovalKind.Fulfill;
}