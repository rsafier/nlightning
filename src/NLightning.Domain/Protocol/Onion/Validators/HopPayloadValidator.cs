using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Protocol.Onion.Validators;

using Constants;
using Exceptions;
using Factories;
using Models;
using Protocol.ValueObjects;

/// <summary>
/// Pure semantic checks of a parsed BOLT 4 hop payload (the "reader" requirements of the <c>payload</c> format).
/// </summary>
/// <remarks>
/// <para>
/// Every violation is reported as <c>invalid_onion_payload</c> (PERM|22) with data <c>bigsize type || u16 offset</c>,
/// where <c>type</c> is the offending (or missing) record and <c>offset</c> is where that record started in the
/// decrypted byte stream (0 when the record is missing or the offset is unknown).
/// </para>
/// <para>
/// BOLT 4 "Returning Errors": inside a blinded route (a <c>path_key</c> in <c>update_add_htlc</c>, or a
/// <c>current_path_key</c> at a non-final node) the erring node MUST return <c>invalid_onion_blinding</c> instead.
/// That mapping, and the checks that need <c>encrypted_recipient_data</c> decrypted (payment_relay,
/// payment_constraints, allowed_features), belong to the route-blinding processor (M5).
/// </para>
/// <para>
/// Outside a blinded route, unknown odd records are ignored, as BOLT 1 allows. For blinded hops BOLT 4 says the
/// reader "MUST return an error if the payload contains other tlv fields" than the allowed ones, with no exception
/// for unknown odd types, so every record (known or not) is checked against the allowed set.
/// </para>
/// <para>
/// Custom records (types of 65536 and up, <see cref="OnionPayloadTlvTypes.CustomRecordTypeStart"/>) are accepted
/// whatever their parity at a final hop, as LND does (keysend's own record is even), and a non-blinded final hop with a
/// <c>keysend_preimage</c> needs no <c>payment_data</c> (keysend, a spontaneous payment without an invoice; lane
/// lh1-l3). This is an interop deviation from BOLT 1 ("if type is even: MUST fail to parse") limited to the final
/// hop, the only place a custom record means anything: a forwarding hop keeps BOLT 1's strictness (LND accepts them at
/// every hop), and a blinded hop allows only its fixed set of types.
/// </para>
/// <para>
/// A non-blinded final hop that carries <c>short_channel_id</c> is accepted: "MUST NOT include short_channel_id" is a
/// writer rule only, and the final-node reader requirements do not check it.
/// </para>
/// <para>
/// Trampoline (BOLTs PR 836, NL-875): the trampoline types (<see cref="OnionPayloadTlvTypes.TrampolineTypes"/>) count
/// as unknown unless the caller passes <c>allowTrampoline</c> (the node advertises <c>trampoline_routing</c>), so a
/// node without the feature refuses a <c>trampoline_onion_packet</c> (20), an <c>outgoing_node_id</c> (14) or
/// <c>recipient_blinded_paths</c> (22) with <c>invalid_onion_payload</c> exactly as before they were known, and
/// ignores the odd <c>recipient_features</c> (21). With <c>allowTrampoline</c>, a non-blinded final hop carrying a
/// <c>trampoline_onion_packet</c> needs only <c>amt_to_forward</c> and <c>outgoing_cltv_value</c> (the outer
/// <c>payment_data</c> is optional when the trampoline node is reached without MPP), may carry the
/// <c>current_path_key</c> of a blinded trampoline hop, and must not carry <c>short_channel_id</c>; a
/// <c>trampoline_onion_packet</c> anywhere else, and <c>outgoing_node_id</c> or <c>recipient_blinded_paths</c> in any
/// payment onion payload, are refused: they belong in the trampoline onion, whose payload
/// <see cref="TrampolinePayloadValidator"/> checks.
/// </para>
/// </remarks>
public static class HopPayloadValidator
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
    /// Validates <paramref name="payload"/> and throws on the first violation, treating the trampoline types as
    /// unknown.
    /// </summary>
    /// <param name="payload">The parsed hop payload.</param>
    /// <param name="isFinalHop">Whether this node is the final destination (the peeled next_hmac is all zero).</param>
    /// <param name="hasUpdateAddPathKey">Whether the incoming <c>update_add_htlc</c> carried a <c>path_key</c>.</param>
    /// <exception cref="OnionException">Thrown with <c>invalid_onion_payload</c> when the payload is invalid.</exception>
    public static void Validate(HopPayload payload, bool isFinalHop, bool hasUpdateAddPathKey)
    {
        Validate(payload, isFinalHop, hasUpdateAddPathKey, false);
    }

    /// <summary>
    /// Validates <paramref name="payload"/> and throws on the first violation.
    /// </summary>
    /// <param name="payload">The parsed hop payload.</param>
    /// <param name="isFinalHop">Whether this node is the final destination (the peeled next_hmac is all zero).</param>
    /// <param name="hasUpdateAddPathKey">Whether the incoming <c>update_add_htlc</c> carried a <c>path_key</c>.</param>
    /// <param name="allowTrampoline">Whether this node processes trampoline payloads (it advertises
    /// <c>trampoline_routing</c>); <c>false</c> treats the trampoline types as unknown.</param>
    /// <exception cref="OnionException">Thrown with <c>invalid_onion_payload</c> when the payload is invalid.</exception>
    public static void Validate(HopPayload payload, bool isFinalHop, bool hasUpdateAddPathKey, bool allowTrampoline)
    {
        if (!TryValidate(payload, isFinalHop, hasUpdateAddPathKey, allowTrampoline, out var error))
            throw error;
    }

    /// <summary>
    /// Validates <paramref name="payload"/> without throwing, treating the trampoline types as unknown.
    /// </summary>
    /// <param name="payload">The parsed hop payload.</param>
    /// <param name="isFinalHop">Whether this node is the final destination (the peeled next_hmac is all zero).</param>
    /// <param name="hasUpdateAddPathKey">Whether the incoming <c>update_add_htlc</c> carried a <c>path_key</c>.</param>
    /// <param name="error">The first violation found, as an <c>invalid_onion_payload</c> exception.</param>
    /// <returns><c>true</c> when the payload is valid.</returns>
    public static bool TryValidate(HopPayload payload, bool isFinalHop, bool hasUpdateAddPathKey,
                                   [NotNullWhen(false)] out OnionException? error)
    {
        return TryValidate(payload, isFinalHop, hasUpdateAddPathKey, false, out error);
    }

    /// <summary>
    /// Validates <paramref name="payload"/> without throwing.
    /// </summary>
    /// <param name="payload">The parsed hop payload.</param>
    /// <param name="isFinalHop">Whether this node is the final destination (the peeled next_hmac is all zero).</param>
    /// <param name="hasUpdateAddPathKey">Whether the incoming <c>update_add_htlc</c> carried a <c>path_key</c>.</param>
    /// <param name="allowTrampoline">Whether this node processes trampoline payloads (it advertises
    /// <c>trampoline_routing</c>); <c>false</c> treats the trampoline types as unknown.</param>
    /// <param name="error">The first violation found, as an <c>invalid_onion_payload</c> exception.</param>
    /// <returns><c>true</c> when the payload is valid.</returns>
    public static bool TryValidate(HopPayload payload, bool isFinalHop, bool hasUpdateAddPathKey,
                                   bool allowTrampoline, [NotNullWhen(false)] out OnionException? error)
    {
        ArgumentNullException.ThrowIfNull(payload);

        error = FindUnknownEvenType(payload, isFinalHop, allowTrampoline);
        if (error is not null)
            return false;

        if (allowTrampoline && HasTrampolineRecord(payload))
            error = ValidateTrampolineRecords(payload, isFinalHop, hasUpdateAddPathKey);
        else if (payload.IsBlinded)
            error = ValidateBlinded(payload, isFinalHop, hasUpdateAddPathKey);
        else
            error = ValidateNonBlinded(payload, isFinalHop, hasUpdateAddPathKey);

        return error is null;
    }

    /// <summary>
    /// <c>invalid_onion_payload</c> for a required record that is missing (offset 0).
    /// </summary>
    internal static OnionException Missing(HopPayload payload, BigSize type, string name)
    {
        return Fail(payload, type, $"Required hop payload field {name} (type {type.Value}) is missing.");
    }

    /// <summary>
    /// <c>invalid_onion_payload</c> for <paramref name="type"/>, at the offset the record started at when known.
    /// </summary>
    internal static OnionException Fail(HopPayload payload, BigSize type, string message)
    {
        var offset = payload.TryGetRecordOffset(type, out var recordOffset) ? recordOffset : 0;
        return InvalidOnionPayloadFailureFactory.Create(type, offset, message);
    }

    private static OnionException? FindUnknownEvenType(HopPayload payload, bool isFinalHop, bool allowTrampoline)
    {
        // BOLT 1: an unknown even type MUST fail the stream. The parser enforces it below the custom range (it cannot
        // tell a final hop); repeat it here for payloads built by hand. Custom records (65536 and up) are accepted
        // whatever their parity at the final hop only, as LND does: they are for the final node's application
        // (keysend's preimage is one, and even). A forwarding hop has no use for them and keeps BOLT 1's rule.
        // The trampoline types are parsed, but count as unknown unless the caller allows trampoline: a node that does
        // not process trampoline payloads refuses them exactly as before they were known (NL-875)
        var unknownEven = payload.Tlvs.FirstOrDefault(tlv => IsUnknown(tlv.Type, allowTrampoline)
                                                          && tlv.Type.Value % 2 == 0
                                                          && (!isFinalHop
                                                           || tlv.Type < OnionPayloadTlvTypes.CustomRecordTypeStart));

        return unknownEven is null
                   ? null
                   : Fail(payload, unknownEven.Type, $"Unknown even TLV type {unknownEven.Type.Value}.");
    }

    private static bool HasTrampolineRecord(HopPayload payload) =>
        payload.TrampolineOnionPacket is not null || payload.OutgoingNodeId is not null
                                                  || payload.RecipientBlindedPaths is not null;

    /// <summary>
    /// The payment onion rules of a payload with a trampoline record (14, 20 or 22), once trampoline is allowed.
    /// </summary>
    private static OnionException? ValidateTrampolineRecords(HopPayload payload, bool isFinalHop,
                                                             bool hasUpdateAddPathKey)
    {
        // outgoing_node_id and recipient_blinded_paths name the next trampoline node or the recipient's paths: they
        // belong in a trampoline onion's payload, never in a payment onion's
        if (payload.OutgoingNodeId is not null)
            return Fail(payload, OnionPayloadTlvTypes.OutgoingNodeId,
                        "outgoing_node_id is only allowed in a trampoline onion payload.");

        if (payload.RecipientBlindedPaths is not null)
            return Fail(payload, OnionPayloadTlvTypes.RecipientBlindedPaths,
                        "recipient_blinded_paths is only allowed in a trampoline onion payload.");

        // BOLT 4 (PR 836): the sender "MUST include the trampoline_onion_packet tlv in the last hop's payload of the
        // onion_packet", which is never inside a blinded route (a blinded path to a trampoline-supporting recipient
        // travels inside the trampoline onion)
        if (!isFinalHop)
            return Fail(payload, OnionPayloadTlvTypes.TrampolineOnionPacket,
                        "trampoline_onion_packet is only allowed in the final hop's payload.");

        if (payload.IsBlinded)
            return Fail(payload, OnionPayloadTlvTypes.TrampolineOnionPacket,
                        "trampoline_onion_packet is not allowed in a blinded hop payload.");

        if (hasUpdateAddPathKey)
            return Missing(payload, OnionPayloadTlvTypes.EncryptedRecipientData, "encrypted_recipient_data");

        if (payload.AmtToForward is null)
            return Missing(payload, OnionPayloadTlvTypes.AmtToForward, "amt_to_forward");

        if (payload.OutgoingCltvValue is null)
            return Missing(payload, OnionPayloadTlvTypes.OutgoingCltvValue, "outgoing_cltv_value");

        // The writer of a final hop "MUST NOT include short_channel_id nor outgoing_node_id": for the trampoline node
        // it is enforced, since a next hop named in the outer payload would contradict the trampoline onion.
        // payment_data is optional ("The outer onion MAY omit payment_data when not using MPP to reach the trampoline
        // node"), and a current_path_key is the path key of a blinded trampoline hop (it must be in exactly one of the
        // two onions, which TrampolinePayloadValidator checks)
        return payload.ShortChannelId is not null
                   ? Fail(payload, OnionPayloadTlvTypes.ShortChannelId,
                          "short_channel_id is not allowed in a payload carrying a trampoline_onion_packet.")
                   : null;
    }

    private static OnionException? ValidateBlinded(HopPayload payload, bool isFinalHop, bool hasUpdateAddPathKey)
    {
        // path_key (update_add_htlc) and current_path_key (payload) are mutually exclusive, and one is required.
        if (hasUpdateAddPathKey && payload.CurrentPathKey is not null)
            return Fail(payload, OnionPayloadTlvTypes.CurrentPathKey,
                        "current_path_key must not be present when update_add_htlc carries a path_key.");

        if (!hasUpdateAddPathKey && payload.CurrentPathKey is null)
            return Fail(payload, OnionPayloadTlvTypes.CurrentPathKey,
                        "current_path_key is required when update_add_htlc carries no path_key.");

        var allowedTypes = isFinalHop ? s_blindedFinalAllowedTypes : s_blindedIntermediateAllowedTypes;
        var forbidden = payload.Tlvs.FirstOrDefault(tlv => !allowedTypes.Contains(tlv.Type));
        if (forbidden is not null)
            return Fail(payload, forbidden.Type,
                        $"TLV type {forbidden.Type.Value} is not allowed in a blinded "
                      + (isFinalHop ? "final" : "intermediate") + " hop payload.");

        if (!isFinalHop)
            return null;

        if (payload.AmtToForward is null)
            return Missing(payload, OnionPayloadTlvTypes.AmtToForward, "amt_to_forward");

        if (payload.OutgoingCltvValue is null)
            return Missing(payload, OnionPayloadTlvTypes.OutgoingCltvValue, "outgoing_cltv_value");

        return payload.TotalAmountMsat is null
                   ? Missing(payload, OnionPayloadTlvTypes.TotalAmountMsat, "total_amount_msat")
                   : null;
    }

    private static OnionException? ValidateNonBlinded(HopPayload payload, bool isFinalHop, bool hasUpdateAddPathKey)
    {
        // Outside a blinded route there must be neither a path_key nor a current_path_key.
        if (hasUpdateAddPathKey)
            return Missing(payload, OnionPayloadTlvTypes.EncryptedRecipientData, "encrypted_recipient_data");

        if (payload.CurrentPathKey is not null)
            return Fail(payload, OnionPayloadTlvTypes.CurrentPathKey,
                        "current_path_key requires encrypted_recipient_data.");

        if (payload.AmtToForward is null)
            return Missing(payload, OnionPayloadTlvTypes.AmtToForward, "amt_to_forward");

        if (payload.OutgoingCltvValue is null)
            return Missing(payload, OnionPayloadTlvTypes.OutgoingCltvValue, "outgoing_cltv_value");

        if (!isFinalHop)
            return payload.ShortChannelId is null
                       ? Missing(payload, OnionPayloadTlvTypes.ShortChannelId, "short_channel_id")
                       : null;

        // A short_channel_id at the final node is ignored: "MUST NOT include" it is a writer-only rule.

        // BOLT 4 reader, final node: "MUST return an error if total_msat is not present". Outside a blinded route
        // total_msat is carried only by payment_data. A keysend payment (keysend_preimage, no invoice, so no
        // payment_secret) is paid in one HTLC without it, as LND and CLN send it: its total is amt_to_forward
        return payload.PaymentData is null && payload.KeysendPreimage is null
                   ? Missing(payload, OnionPayloadTlvTypes.PaymentData, "payment_data (total_msat)")
                   : null;
    }

    private static bool IsUnknown(BigSize type, bool allowTrampoline) =>
        !OnionPayloadTlvTypes.KnownTypes.Contains(type)
     || (!allowTrampoline && OnionPayloadTlvTypes.TrampolineTypes.Contains(type));
}