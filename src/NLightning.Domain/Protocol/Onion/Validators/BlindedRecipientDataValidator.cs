using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Protocol.Onion.Validators;

using Models;

/// <summary>
/// The BOLT 4 reader rules on a decrypted <c>encrypted_recipient_data</c> (payload format, "If
/// <c>encrypted_recipient_data</c> is present"), for a payment.
/// </summary>
/// <remarks>
/// <para>Checks, first failure wins:</para>
/// <list type="number">
///   <item>Both <c>short_channel_id</c> and <c>next_node_id</c> are present.</item>
///   <item><c>allowed_features</c> sets any bit (no feature is defined for blinded payments, so every bit is
///   unknown; a missing record reads as an empty array).</item>
///   <item><c>payment_constraints</c>: the incoming <c>cltv_expiry</c> is above <c>max_cltv_expiry</c>, or the
///   incoming <c>amount_msat</c> is below <c>htlc_minimum_msat</c>.</item>
///   <item>Non-final hop: neither <c>short_channel_id</c> nor <c>next_node_id</c>; no <c>payment_relay</c>.</item>
/// </list>
/// <para>The final hop's <c>path_id</c> is checked by the final-hop processor (it depends on the invoice). Every
/// failure is reported as <c>invalid_onion_blinding</c> by the caller, so only a reason is returned. Pure.</para>
/// </remarks>
public static class BlindedRecipientDataValidator
{
    /// <summary>
    /// Validates <paramref name="recipientData"/> for a hop of a blinded payment route.
    /// </summary>
    /// <param name="recipientData">The decoded records.</param>
    /// <param name="isFinalHop">Whether the onion ends here.</param>
    /// <param name="incomingAmountMsat">The incoming HTLC's <c>amount_msat</c>, or null to skip the amount
    /// constraint.</param>
    /// <param name="incomingCltvExpiry">The incoming HTLC's <c>cltv_expiry</c>, or null to skip the expiry
    /// constraint.</param>
    /// <param name="reason">Why the data is refused.</param>
    public static bool TryValidate(BlindedRecipientData recipientData, bool isFinalHop, ulong? incomingAmountMsat,
                                   uint? incomingCltvExpiry, [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(recipientData);

        reason = FindFailure(recipientData, isFinalHop, incomingAmountMsat, incomingCltvExpiry);
        return reason is null;
    }

    private static string? FindFailure(BlindedRecipientData data, bool isFinalHop, ulong? incomingAmountMsat,
                                       uint? incomingCltvExpiry)
    {
        if (data.ShortChannelId is not null && data.NextNodeId is not null)
            return "encrypted_recipient_data contains both short_channel_id and next_node_id.";

        if (data.HasAnyAllowedFeature)
            return "encrypted_recipient_data allows a feature we do not know.";

        if (data.PaymentConstraints is { } constraints)
        {
            if (incomingCltvExpiry is { } expiry && expiry > constraints.MaxCltvExpiry)
                return $"cltv_expiry {expiry} is above payment_constraints.max_cltv_expiry "
                     + $"{constraints.MaxCltvExpiry}.";

            if (incomingAmountMsat is { } amount && amount < constraints.HtlcMinimumMsat)
                return $"amount_msat {amount} is below payment_constraints.htlc_minimum_msat "
                     + $"{constraints.HtlcMinimumMsat}.";
        }

        if (isFinalHop)
            return null;

        if (data.ShortChannelId is null && data.NextNodeId is null)
            return "encrypted_recipient_data of a non-final hop has neither short_channel_id nor next_node_id.";

        return data.PaymentRelay is null
                   ? "encrypted_recipient_data of a non-final hop has no payment_relay."
                   : null;
    }
}