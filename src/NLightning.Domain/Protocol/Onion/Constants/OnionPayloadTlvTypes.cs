using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Protocol.Onion.Constants;

using Protocol.ValueObjects;

/// <summary>
/// TLV types of the BOLT 4 hop payload (<c>payload</c>) TLV stream.
/// </summary>
/// <remarks>
/// These numbers live in their own namespace and must not be mixed with <c>TlvConstants</c>.
/// </remarks>
[ExcludeFromCodeCoverage]
public static class OnionPayloadTlvTypes
{
    /// <summary>
    /// amt_to_forward (tu64).
    /// </summary>
    public static readonly BigSize AmtToForward = 2;

    /// <summary>
    /// outgoing_cltv_value (tu32).
    /// </summary>
    public static readonly BigSize OutgoingCltvValue = 4;

    /// <summary>
    /// short_channel_id (8 bytes).
    /// </summary>
    public static readonly BigSize ShortChannelId = 6;

    /// <summary>
    /// payment_data (32-byte payment_secret || tu64 total_msat).
    /// </summary>
    public static readonly BigSize PaymentData = 8;

    /// <summary>
    /// encrypted_recipient_data (variable bytes).
    /// </summary>
    public static readonly BigSize EncryptedRecipientData = 10;

    /// <summary>
    /// current_path_key (point).
    /// </summary>
    public static readonly BigSize CurrentPathKey = 12;

    /// <summary>
    /// payment_metadata (variable bytes).
    /// </summary>
    public static readonly BigSize PaymentMetadata = 16;

    /// <summary>
    /// total_amount_msat (tu64).
    /// </summary>
    public static readonly BigSize TotalAmountMsat = 18;

    /// <summary>
    /// The first type of the custom-record range (65536, LND's <c>record.CustomTypeStart</c>): types from here on are
    /// application records for the final node (keysend, podcasting 2.0 boostagrams, ...), not BOLT 4 fields.
    /// </summary>
    /// <remarks>
    /// A record in this range is kept verbatim whatever its parity: like LND ("we always accept custom fields, because a
    /// higher level application may understand them") the parser and <c>HopPayloadValidator</c> do not fail an unknown
    /// even type here (BOLT 1's "it's ok to be odd" rule applies below it). They are not in <see cref="KnownTypes"/>.
    /// </remarks>
    public static readonly BigSize CustomRecordTypeStart = 65536;

    /// <summary>
    /// keysend_preimage (5482373484, 32 bytes): the preimage of a spontaneous payment (keysend), in the final hop's
    /// custom-record range. Not a BOLT type; LND, CLN and Eclair use it.
    /// </summary>
    public static readonly BigSize KeysendPreimage = 5482373484;

    /// <summary>
    /// Every type of the <c>payload</c> namespace this node understands. Any other even type fails the payload
    /// (BOLT 1 "it's ok to be odd").
    /// </summary>
    /// <remarks>Declared last so the fields above are initialized first.</remarks>
    public static readonly IReadOnlySet<BigSize> KnownTypes = new[]
    {
        AmtToForward, OutgoingCltvValue, ShortChannelId, PaymentData, EncryptedRecipientData, CurrentPathKey,
        PaymentMetadata, TotalAmountMsat
    }.ToFrozenSet();
}