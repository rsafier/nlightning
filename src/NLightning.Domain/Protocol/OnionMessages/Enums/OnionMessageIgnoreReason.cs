namespace NLightning.Domain.Protocol.OnionMessages.Enums;

using Interfaces;

/// <summary>
/// The BOLT 4 reader rule that made <see cref="IOnionMessageUnwrapper"/> ignore an <c>onion_message</c>.
/// </summary>
public enum OnionMessageIgnoreReason
{
    /// <summary>
    /// The packet does not parse or peel (bad version, key, HMAC or length).
    /// </summary>
    Undecryptable,

    /// <summary>
    /// The <c>onionmsg_tlv</c> is invalid, has an unknown even type, or its <c>reply_path</c> is not a valid
    /// <c>blinded_path</c>.
    /// </summary>
    InvalidPayload,

    /// <summary>
    /// No <c>encrypted_recipient_data</c>, or it does not decrypt.
    /// </summary>
    InvalidRecipientData,

    /// <summary>
    /// The recipient data breaks a message-path rule: an <c>allowed_features</c> bit (none is defined for onion
    /// messages), or <c>payment_relay</c>/<c>payment_constraints</c>.
    /// </summary>
    ForbiddenRecipientData,

    /// <summary>
    /// A non-final hop carries more than <c>encrypted_recipient_data</c>.
    /// </summary>
    NonFinalExtraFields,

    /// <summary>
    /// A non-final hop's recipient data has a <c>path_id</c>.
    /// </summary>
    NonFinalPathId,

    /// <summary>
    /// A non-final hop's recipient data names no next node (neither <c>next_node_id</c> nor
    /// <c>short_channel_id</c>).
    /// </summary>
    NoNextHop,

    /// <summary>
    /// A final hop with more than one payload field (types 64 and up).
    /// </summary>
    MultiplePayloadFields
}