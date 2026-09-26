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
/// A non-blinded final hop that carries <c>short_channel_id</c> is accepted: "MUST NOT include short_channel_id" is a
/// writer rule only, and the final-node reader requirements do not check it.
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
    /// Validates <paramref name="payload"/> and throws on the first violation.
    /// </summary>
    /// <param name="payload">The parsed hop payload.</param>
    /// <param name="isFinalHop">Whether this node is the final destination (the peeled next_hmac is all zero).</param>
    /// <param name="hasUpdateAddPathKey">Whether the incoming <c>update_add_htlc</c> carried a <c>path_key</c>.</param>
    /// <exception cref="OnionException">Thrown with <c>invalid_onion_payload</c> when the payload is invalid.</exception>
    public static void Validate(HopPayload payload, bool isFinalHop, bool hasUpdateAddPathKey)
    {
        if (!TryValidate(payload, isFinalHop, hasUpdateAddPathKey, out var error))
            throw error;
    }

    /// <summary>
    /// Validates <paramref name="payload"/> without throwing.
    /// </summary>
    /// <param name="payload">The parsed hop payload.</param>
    /// <param name="isFinalHop">Whether this node is the final destination (the peeled next_hmac is all zero).</param>
    /// <param name="hasUpdateAddPathKey">Whether the incoming <c>update_add_htlc</c> carried a <c>path_key</c>.</param>
    /// <param name="error">The first violation found, as an <c>invalid_onion_payload</c> exception.</param>
    /// <returns><c>true</c> when the payload is valid.</returns>
    public static bool TryValidate(HopPayload payload, bool isFinalHop, bool hasUpdateAddPathKey,
                                   [NotNullWhen(false)] out OnionException? error)
    {
        ArgumentNullException.ThrowIfNull(payload);

        error = FindUnknownEvenType(payload)
             ?? (payload.IsBlinded
                     ? ValidateBlinded(payload, isFinalHop, hasUpdateAddPathKey)
                     : ValidateNonBlinded(payload, isFinalHop, hasUpdateAddPathKey));

        return error is null;
    }

    private static OnionException? FindUnknownEvenType(HopPayload payload)
    {
        // BOLT 1: an unknown even type MUST fail the stream. The parser already enforces this; repeat it here for
        // payloads that were built by hand.
        var unknownEven = payload.UnknownTlvs.FirstOrDefault(tlv => tlv.Type.Value % 2 == 0);

        return unknownEven is null
                   ? null
                   : Fail(payload, unknownEven.Type, $"Unknown even TLV type {unknownEven.Type.Value}.");
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
        // total_msat is carried only by payment_data.
        return payload.PaymentData is null
                   ? Missing(payload, OnionPayloadTlvTypes.PaymentData, "payment_data (total_msat)")
                   : null;
    }

    private static OnionException Missing(HopPayload payload, BigSize type, string name)
    {
        return Fail(payload, type, $"Required hop payload field {name} (type {type.Value}) is missing.");
    }

    private static OnionException Fail(HopPayload payload, BigSize type, string message)
    {
        var offset = payload.TryGetRecordOffset(type, out var recordOffset) ? recordOffset : 0;
        return InvalidOnionPayloadFailureFactory.Create(type, offset, message);
    }
}