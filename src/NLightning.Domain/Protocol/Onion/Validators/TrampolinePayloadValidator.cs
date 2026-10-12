using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Protocol.Onion.Validators;

using Constants;
using Enums;
using Exceptions;
using Models;
using Money;
using Protocol.ValueObjects;

/// <summary>
/// Pure checks of a peeled trampoline onion payload (the "inner" payload, BOLTs PR 836 "Trampoline Payments") against
/// its own rules and against the payment onion payload (the "outer" one) that carried the
/// <c>trampoline_onion_packet</c>.
/// </summary>
/// <remarks>
/// <para>
/// The trampoline payload uses the <c>payload</c> TLV namespace. Payload rules are reported as
/// <c>invalid_onion_payload</c> with the offending (or missing) inner record's type and its offset in the decrypted
/// trampoline payload (0 when missing or unknown), like <see cref="HopPayloadValidator"/>. Unknown even types fail
/// below the custom range (65536 and up, accepted at the final trampoline hop only, as for a payment onion), unknown
/// odd ones are ignored outside a blinded route; a trampoline payload never carries another
/// <c>trampoline_onion_packet</c>.
/// </para>
/// <list type="bullet">
/// <item>Final, not blinded, without <c>recipient_blinded_paths</c> (a BOLT 11 recipient): <c>amt_to_forward</c>,
/// <c>outgoing_cltv_value</c> and <c>payment_data</c> (the invoice's secret and total); <c>payment_metadata</c>
/// optional; no <c>short_channel_id</c>, <c>outgoing_node_id</c> or <c>recipient_features</c>.</item>
/// <item>Intermediate, not blinded, and the last layer when it names <c>recipient_blinded_paths</c> (the payer's
/// trampoline onion ends with the last trampoline node's payload, which pays the recipient's blinded paths, as in
/// <c>trampoline-to-blinded-path-payment-onion-test.json</c>): <c>amt_to_forward</c>, <c>outgoing_cltv_value</c> and
/// exactly one of <c>outgoing_node_id</c> (the next trampoline node) or a non-empty <c>recipient_blinded_paths</c> (a
/// recipient that does not support trampoline); <c>recipient_features</c> only with the latter. A
/// <c>short_channel_id</c> is ignored (the writer "MUST use outgoing_node_id instead").</item>
/// <item>Blinded (<c>encrypted_recipient_data</c> present; BOLT 12 recipients that support trampoline): exactly one of
/// the inner payload's <c>current_path_key</c> (the introduction node) and the outer one's (a later node) is present;
/// an intermediate hop carries only <c>encrypted_recipient_data</c> and <c>current_path_key</c>, the final hop also
/// requires <c>amt_to_forward</c>, <c>outgoing_cltv_value</c> and <c>total_amount_msat</c> and nothing else. The
/// <c>invalid_onion_blinding</c> remap (every hop but the introduction node) is the caller's, as for a payment
/// onion.</item>
/// <item>Cross-onion (BOLTs PR 836, intermediate and final trampoline nodes): an outer <c>outgoing_cltv_value</c> below
/// the inner one is <c>final_incorrect_cltv_expiry</c> (data: the outer value), an outer total below the inner
/// <c>amt_to_forward</c> is <c>final_incorrect_htlc_amount</c> (data: the outer total). The outer total is its
/// <c>payment_data</c> total, else its <c>total_amount_msat</c>, else its <c>amt_to_forward</c> (the outer onion may
/// omit <c>payment_data</c> without MPP). A blinded intermediate hop has no amount or expiry of its own before
/// <c>payment_relay</c> is decrypted, so the caller makes those checks then.</item>
/// </list>
/// <para>
/// The outer payload must have passed <see cref="HopPayloadValidator"/> with <c>allowTrampoline</c> first.
/// </para>
/// </remarks>
public static class TrampolinePayloadValidator
{
    private static readonly HashSet<BigSize> s_blindedIntermediateAllowedTypes =
    [
        OnionPayloadTlvTypes.EncryptedRecipientData,
        OnionPayloadTlvTypes.CurrentPathKey
    ];

    private static readonly HashSet<BigSize> s_blindedFinalAllowedTypes =
    [
        OnionPayloadTlvTypes.AmtToForward,
        OnionPayloadTlvTypes.OutgoingCltvValue,
        OnionPayloadTlvTypes.EncryptedRecipientData,
        OnionPayloadTlvTypes.CurrentPathKey,
        OnionPayloadTlvTypes.TotalAmountMsat
    ];

    /// <summary>
    /// Validates <paramref name="inner"/> against its rules and <paramref name="outer"/>, and throws on the first
    /// violation.
    /// </summary>
    /// <param name="inner">The peeled trampoline payload.</param>
    /// <param name="isFinal">Whether this node is the last trampoline hop (the trampoline onion's next_hmac is all
    /// zero).</param>
    /// <param name="outer">The payment onion payload that carried the <c>trampoline_onion_packet</c>.</param>
    /// <param name="outerHasPathKey">Whether the outer onion supplied this hop's path key from outside the outer
    /// payload; the outer payload's own <c>current_path_key</c> counts in any case.</param>
    /// <exception cref="OnionException">Thrown on the first violation (see the class remarks for the codes).</exception>
    public static void Validate(HopPayload inner, bool isFinal, HopPayload outer, bool outerHasPathKey)
    {
        if (!TryValidate(inner, isFinal, outer, outerHasPathKey, out var error))
            throw error;
    }

    /// <summary>
    /// Validates <paramref name="inner"/> against its rules and <paramref name="outer"/> without throwing.
    /// </summary>
    /// <param name="inner">The peeled trampoline payload.</param>
    /// <param name="isFinal">Whether this node is the last trampoline hop (the trampoline onion's next_hmac is all
    /// zero).</param>
    /// <param name="outer">The payment onion payload that carried the <c>trampoline_onion_packet</c>.</param>
    /// <param name="outerHasPathKey">Whether the outer onion supplied this hop's path key from outside the outer
    /// payload; the outer payload's own <c>current_path_key</c> counts in any case.</param>
    /// <param name="error">The first violation found.</param>
    /// <returns><c>true</c> when the payload is valid.</returns>
    public static bool TryValidate(HopPayload inner, bool isFinal, HopPayload outer, bool outerHasPathKey,
                                   [NotNullWhen(false)] out OnionException? error)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(outer);

        var outerPathKey = outerHasPathKey || outer.CurrentPathKey is not null;

        // A last layer naming recipient_blinded_paths is the last trampoline node's, not the recipient's
        var isRecipient = isFinal && (inner.IsBlinded || inner.RecipientBlindedPaths is null);
        error = FindUnknownEvenType(inner, isRecipient)
             ?? (inner.TrampolineOnionPacket is not null
                     ? HopPayloadValidator.Fail(inner, OnionPayloadTlvTypes.TrampolineOnionPacket,
                                                "A trampoline payload cannot carry another trampoline_onion_packet.")
                     : null)
             ?? (inner.IsBlinded
                     ? ValidateBlinded(inner, isFinal, outerPathKey)
                     : ValidateNonBlinded(inner, isRecipient, outerPathKey))
             ?? ValidateAgainstOuter(inner, outer);

        return error is null;
    }

    /// <summary>
    /// The total the outer onion promises the trampoline node: its <c>payment_data</c> total, else its
    /// <c>total_amount_msat</c>, else its <c>amt_to_forward</c>; null when the outer payload has none of them.
    /// </summary>
    public static LightningMoney? GetOuterTotal(HopPayload outer)
    {
        ArgumentNullException.ThrowIfNull(outer);
        return outer.PaymentData?.TotalMsat ?? outer.TotalAmountMsat ?? outer.AmtToForward;
    }

    private static OnionException? FindUnknownEvenType(HopPayload inner, bool isFinal)
    {
        var unknownEven = inner.UnknownTlvs.FirstOrDefault(tlv => tlv.Type.Value % 2 == 0
                                                               && (!isFinal
                                                                || tlv.Type < OnionPayloadTlvTypes
                                                                              .CustomRecordTypeStart));

        return unknownEven is null
                   ? null
                   : HopPayloadValidator.Fail(inner, unknownEven.Type,
                                              $"Unknown even TLV type {unknownEven.Type.Value} in a trampoline "
                                            + "payload.");
    }

    private static OnionException? ValidateBlinded(HopPayload inner, bool isFinal, bool outerPathKey)
    {
        // BOLTs PR 836: "If current_path_key is not set in the trampoline onion or the outer onion: MUST reject",
        // "If current_path_key is set in both: MUST reject"
        if (inner.CurrentPathKey is not null && outerPathKey)
            return HopPayloadValidator.Fail(inner, OnionPayloadTlvTypes.CurrentPathKey,
                                            "current_path_key is set in both the trampoline and the outer onion.");

        if (inner.CurrentPathKey is null && !outerPathKey)
            return HopPayloadValidator.Fail(inner, OnionPayloadTlvTypes.CurrentPathKey,
                                            "current_path_key is set in neither the trampoline nor the outer onion.");

        // "MUST NOT include any other field"
        var allowedTypes = isFinal ? s_blindedFinalAllowedTypes : s_blindedIntermediateAllowedTypes;
        var forbidden = inner.Tlvs.FirstOrDefault(tlv => !allowedTypes.Contains(tlv.Type));
        if (forbidden is not null)
            return HopPayloadValidator.Fail(inner, forbidden.Type,
                                            $"TLV type {forbidden.Type.Value} is not allowed in a blinded "
                                          + (isFinal ? "final" : "intermediate") + " trampoline payload.");

        if (!isFinal)
            return null;

        // "For the final node: MUST include amt_to_forward, outgoing_cltv_value and total_amount_msat"
        if (inner.AmtToForward is null)
            return HopPayloadValidator.Missing(inner, OnionPayloadTlvTypes.AmtToForward, "amt_to_forward");

        if (inner.OutgoingCltvValue is null)
            return HopPayloadValidator.Missing(inner, OnionPayloadTlvTypes.OutgoingCltvValue, "outgoing_cltv_value");

        return inner.TotalAmountMsat is null
                   ? HopPayloadValidator.Missing(inner, OnionPayloadTlvTypes.TotalAmountMsat, "total_amount_msat")
                   : null;
    }

    private static OnionException? ValidateNonBlinded(HopPayload inner, bool isRecipient, bool outerPathKey)
    {
        // Outside a blinded route there is no path key in either onion
        if (outerPathKey)
            return HopPayloadValidator.Missing(inner, OnionPayloadTlvTypes.EncryptedRecipientData,
                                               "encrypted_recipient_data");

        if (inner.CurrentPathKey is not null)
            return HopPayloadValidator.Fail(inner, OnionPayloadTlvTypes.CurrentPathKey,
                                            "current_path_key requires encrypted_recipient_data.");

        // "MUST include amt_to_forward and outgoing_cltv_value for each hop"
        if (inner.AmtToForward is null)
            return HopPayloadValidator.Missing(inner, OnionPayloadTlvTypes.AmtToForward, "amt_to_forward");

        if (inner.OutgoingCltvValue is null)
            return HopPayloadValidator.Missing(inner, OnionPayloadTlvTypes.OutgoingCltvValue, "outgoing_cltv_value");

        return isRecipient ? ValidateFinal(inner) : ValidateIntermediate(inner);
    }

    private static OnionException? ValidateFinal(HopPayload inner)
    {
        // The final node "MUST NOT include short_channel_id nor outgoing_node_id"; recipient_features goes with
        // recipient_blinded_paths, which make the last layer the last trampoline node's (validated as a relay)
        if (inner.ShortChannelId is not null)
            return HopPayloadValidator.Fail(inner, OnionPayloadTlvTypes.ShortChannelId,
                                            "short_channel_id is not allowed in a final trampoline payload.");

        if (inner.OutgoingNodeId is not null)
            return HopPayloadValidator.Fail(inner, OnionPayloadTlvTypes.OutgoingNodeId,
                                            "outgoing_node_id is not allowed in a final trampoline payload.");

        if (inner.RecipientFeatures is not null)
            return HopPayloadValidator.Fail(inner, OnionPayloadTlvTypes.RecipientFeatures,
                                            "recipient_features is not allowed in a final trampoline payload.");

        // "MUST include the invoice's payment_secret in the last trampoline hop's payload"
        return inner.PaymentData is null
                   ? HopPayloadValidator.Missing(inner, OnionPayloadTlvTypes.PaymentData, "payment_data")
                   : null;
    }

    private static OnionException? ValidateIntermediate(HopPayload inner)
    {
        // "MUST include short_channel_id or outgoing_node_id" (non-final), and for a recipient that does not support
        // trampoline the payload carries recipient_blinded_paths instead and "MUST NOT include outgoing_node_id"
        if (inner.OutgoingNodeId is not null && inner.RecipientBlindedPaths is not null)
            return HopPayloadValidator.Fail(inner, OnionPayloadTlvTypes.RecipientBlindedPaths,
                                            "recipient_blinded_paths is not allowed with outgoing_node_id.");

        if (inner.OutgoingNodeId is null && inner.RecipientBlindedPaths is null)
            return HopPayloadValidator.Missing(inner, OnionPayloadTlvTypes.OutgoingNodeId,
                                               "outgoing_node_id or recipient_blinded_paths");

        if (inner.RecipientFeatures is not null && inner.RecipientBlindedPaths is null)
            return HopPayloadValidator.Fail(inner, OnionPayloadTlvTypes.RecipientFeatures,
                                            "recipient_features is only allowed with recipient_blinded_paths.");

        return inner.RecipientBlindedPaths is { Count: 0 }
                   ? HopPayloadValidator.Fail(inner, OnionPayloadTlvTypes.RecipientBlindedPaths,
                                              "recipient_blinded_paths holds no path.")
                   : null;
    }

    private static OnionException? ValidateAgainstOuter(HopPayload inner, HopPayload outer)
    {
        // BOLTs PR 836, intermediate and final trampoline nodes: "If the outer onion's outgoing_cltv_value is smaller
        // than the trampoline onion's outgoing_cltv_value: MUST reject"
        if (inner.OutgoingCltvValue is { } innerCltv && outer.OutgoingCltvValue is { } outerCltv
                                                     && outerCltv < innerCltv)
            return new OnionException(FailureCode.FinalIncorrectCltvExpiry,
                                      $"The outer outgoing_cltv_value {outerCltv} is below the trampoline onion's "
                                    + $"{innerCltv}.",
                                      FailureMessage.FinalIncorrectCltvExpiry(outerCltv).Data);

        // "If the outer onion's total_msat is smaller than the trampoline onion's amt_to_forward: MUST reject"
        if (inner.AmtToForward is { } innerAmount && GetOuterTotal(outer) is { } outerTotal
                                                  && outerTotal < innerAmount)
            return new OnionException(FailureCode.FinalIncorrectHtlcAmount,
                                      $"The outer total {outerTotal.MilliSatoshi} msat is below the trampoline onion's "
                                    + $"amt_to_forward {innerAmount.MilliSatoshi} msat.",
                                      FailureMessage.FinalIncorrectHtlcAmount(outerTotal).Data);

        return null;
    }
}