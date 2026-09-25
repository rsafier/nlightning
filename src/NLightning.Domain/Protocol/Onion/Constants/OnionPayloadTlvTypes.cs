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
}