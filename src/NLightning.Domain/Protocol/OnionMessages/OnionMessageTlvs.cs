namespace NLightning.Domain.Protocol.OnionMessages;

/// <summary>
/// A decoded <c>onionmsg_tlv</c> stream, the payload of one onion-message hop (BOLT 4 "Onion Messages").
/// </summary>
/// <remarks>
/// <para>Shape only: the strict codec (increasing types, minimal bigsize, unknown even types rejected, unknown odd
/// types kept) is lane M6-A's <c>OnionMessageTlvsCodec</c>.</para>
/// <para>A non-final hop MUST carry only <see cref="EncryptedRecipientData"/> (BOLT 4 writer); a reader ignores a
/// non-final hop with anything else.</para>
/// </remarks>
/// <param name="ReplyPath">Type 2 <c>reply_path</c>, or null.</param>
/// <param name="EncryptedRecipientData">Type 4 <c>encrypted_recipient_data</c>, or null.</param>
/// <param name="OtherRecords">Every other record in ascending type order: the final-hop payload fields (64
/// <c>invoice_request</c>, 66 <c>invoice</c>, 68 <c>invoice_error</c>) and the unknown odd records, verbatim.</param>
public sealed record OnionMessageTlvs(WireBlindedPath? ReplyPath, ReadOnlyMemory<byte>? EncryptedRecipientData,
                                      IReadOnlyList<OnionMessageTlvRecord> OtherRecords);