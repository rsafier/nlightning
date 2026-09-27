using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Protocol.OnionMessages.Constants;

/// <summary>
/// Constants of BOLT 4 onion messages: the <c>onionmsg_tlv</c> types and the packet sizes.
/// </summary>
[ExcludeFromCodeCoverage]
public static class OnionMessageConstants
{
    /// <summary>
    /// <c>onionmsg_tlv</c> type 2, <c>reply_path</c> (a <c>blinded_path</c>). Final hop only.
    /// </summary>
    public const ulong ReplyPathType = 2;

    /// <summary>
    /// <c>onionmsg_tlv</c> type 4, <c>encrypted_recipient_data</c>. The only record a non-final hop may carry.
    /// </summary>
    public const ulong EncryptedRecipientDataType = 4;

    /// <summary>
    /// <c>onionmsg_tlv</c> type 64, <c>invoice_request</c> (BOLT 12).
    /// </summary>
    public const ulong InvoiceRequestType = 64;

    /// <summary>
    /// <c>onionmsg_tlv</c> type 66, <c>invoice</c> (BOLT 12).
    /// </summary>
    public const ulong InvoiceType = 66;

    /// <summary>
    /// <c>onionmsg_tlv</c> type 68, <c>invoice_error</c> (BOLT 12).
    /// </summary>
    public const ulong InvoiceErrorType = 68;

    /// <summary>
    /// Types from 64 up are final-hop payload fields; a final hop with more than one of them is ignored.
    /// </summary>
    public const ulong FirstPayloadFieldType = 64;

    /// <summary>
    /// The <c>onionmsg_payloads</c> length a writer SHOULD use when the hops fit (<c>len</c> = 1366).
    /// </summary>
    public const int SmallPayloadsLength = 1300;

    /// <summary>
    /// The <c>onionmsg_payloads</c> length a writer SHOULD use otherwise (<c>len</c> = 32834). Nothing longer is sent.
    /// </summary>
    public const int LargePayloadsLength = 32768;
}