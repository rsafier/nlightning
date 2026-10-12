using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Protocol.OnionMessages;

using Onion.Models;

/// <summary>
/// The BOLT 4 onion-message rules on a hop's <c>onionmsg_tlv</c> and on its decrypted <c>encrypted_data_tlv</c>
/// (<see cref="BlindedRecipientData"/>), for readers (we got the message) and for creators (we build a message path or
/// a <c>reply_path</c>).
/// </summary>
/// <remarks>
/// <para>These are the message-path rules; M5's <c>BlindedRecipientDataValidator</c> holds the payment ones
/// (<c>payment_relay</c> required there, forbidden here). Every reader failure means "ignore the message": onion
/// messages have no error replies (OM-R-01). Pure; the first failure wins and only a reason is returned.</para>
/// <para>The next peer of a non-final hop is <see cref="BlindedRecipientData.NextNodeId"/> when present, else
/// <see cref="BlindedRecipientData.ShortChannelId"/> (an announced SCID or a local alias; OM-R-05). Both present is
/// valid for a message (unlike a payment): <c>next_node_id</c> wins. BOLT 4 has no reader rule against
/// <c>payment_relay</c> or <c>payment_constraints</c> in a message path, so the reader ignores them.</para>
/// </remarks>
public static class MessagePathRecipientDataRules
{
    /// <summary>
    /// The reader rules on the <c>onionmsg_tlv</c> of a hop that forwards (the onion does not end here): it carries
    /// <c>encrypted_recipient_data</c> and nothing else (OM-R-02, OM-R-04).
    /// </summary>
    /// <param name="tlvs">The decoded payload.</param>
    /// <param name="reason">Why the message is ignored.</param>
    public static bool TryValidateNonFinalPayload(OnionMessageTlvs tlvs, [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(tlvs);

        if (tlvs.EncryptedRecipientData is null)
            reason = "onionmsg_tlv of a non-final hop has no encrypted_recipient_data.";
        else if (tlvs.ReplyPath is not null || tlvs.OtherRecords.Count > 0)
            reason = "onionmsg_tlv of a non-final hop carries fields other than encrypted_recipient_data.";
        else
            reason = null;

        return reason is null;
    }

    /// <summary>
    /// The reader rules on the <c>onionmsg_tlv</c> of the final hop: it carries <c>encrypted_recipient_data</c>
    /// (every onion message is blinded, OM-R-02) and at most one payload field (types from 64 up, OM-R-07).
    /// </summary>
    /// <param name="tlvs">The decoded payload.</param>
    /// <param name="reason">Why the message is ignored.</param>
    public static bool TryValidateFinalPayload(OnionMessageTlvs tlvs, [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(tlvs);

        if (tlvs.EncryptedRecipientData is null)
            reason = "onionmsg_tlv of the final hop has no encrypted_recipient_data.";
        else if (OnionMessageTlvsCodec.CountPayloadFields(tlvs) > 1)
            reason = "onionmsg_tlv of the final hop carries more than one payload field.";
        else
            reason = null;

        return reason is null;
    }

    /// <summary>
    /// The reader rules on a decrypted <c>encrypted_data_tlv</c> of a message path.
    /// </summary>
    /// <remarks>
    /// Checks, first failure wins: <c>allowed_features</c> sets any bit (no feature is defined for onion messages,
    /// so every bit is unknown, even an odd one; OM-R-03); then, for a non-final hop, a <c>path_id</c> (OM-R-04) and
    /// neither <c>next_node_id</c> nor <c>short_channel_id</c> (OM-R-05). The final hop's <c>path_id</c> is checked
    /// by the caller against the reply paths it published (OM-R-06).
    /// </remarks>
    /// <param name="recipientData">The decoded records.</param>
    /// <param name="isFinalHop">Whether the onion ends here.</param>
    /// <param name="reason">Why the message is ignored.</param>
    public static bool TryValidateForReader(BlindedRecipientData recipientData, bool isFinalHop,
                                            [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(recipientData);

        if (recipientData.HasAnyAllowedFeature)
            reason = "encrypted_recipient_data allows a feature we do not know.";
        else if (isFinalHop)
            reason = null;
        else if (recipientData.PathId is not null)
            reason = "encrypted_recipient_data of a non-final hop has a path_id.";
        else if (recipientData.NextNodeId is null && recipientData.ShortChannelId is null)
            reason = "encrypted_recipient_data of a non-final hop has neither next_node_id nor short_channel_id.";
        else
            reason = null;

        return reason is null;
    }

    /// <summary>
    /// The creator rules on an <c>encrypted_data_tlv</c> we are about to encrypt for a hop of a message path
    /// (a <c>reply_path</c>, an offer path, or the blinded prefix we build to send).
    /// </summary>
    /// <remarks>
    /// Checks, first failure wins: <c>payment_relay</c> or <c>payment_constraints</c> present (OM-S-04);
    /// <c>allowed_features</c> sets any bit (every reader would ignore the message, OM-R-03); then, for a non-final
    /// hop, a <c>path_id</c> (OM-R-04) and neither <c>next_node_id</c> nor <c>short_channel_id</c> (OM-S-04).
    /// </remarks>
    /// <param name="recipientData">The records to encrypt.</param>
    /// <param name="isFinalHop">Whether the hop is the path's last.</param>
    /// <param name="reason">Why the records must not be sent.</param>
    public static bool TryValidateForWriter(BlindedRecipientData recipientData, bool isFinalHop,
                                            [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(recipientData);

        if (recipientData.PaymentRelay is not null)
            reason = "encrypted_data_tlv of a message path must not include payment_relay.";
        else if (recipientData.PaymentConstraints is not null)
            reason = "encrypted_data_tlv of a message path must not include payment_constraints.";
        else if (recipientData.HasAnyAllowedFeature)
            reason = "encrypted_data_tlv of a message path must not allow features: none is defined.";
        else if (isFinalHop)
            reason = null;
        else if (recipientData.PathId is not null)
            reason = "encrypted_data_tlv of a non-final hop must not include path_id.";
        else if (recipientData.NextNodeId is null && recipientData.ShortChannelId is null)
            reason = "encrypted_data_tlv of a non-final hop must include next_node_id or short_channel_id.";
        else
            reason = null;

        return reason is null;
    }
}