namespace NLightning.Domain.Protocol.OnionMessages;

/// <summary>
/// What an onion message carries to its final hop, besides <c>encrypted_recipient_data</c> and <c>reply_path</c>
/// (BOLT 4 "Onion Messages"): normally exactly one payload field (64 <c>invoice_request</c>, 66 <c>invoice</c>,
/// 68 <c>invoice_error</c>, or an odd type from 64 up in tests), plus any odd records.
/// </summary>
/// <remarks>
/// The bytes are opaque here: onion messages never interpret BOLT 12 payloads.
/// </remarks>
/// <param name="Records">The records, in ascending type order, without types 2 and 4.</param>
public sealed record OnionMessageContents(IReadOnlyList<OnionMessageTlvRecord> Records)
{
    /// <summary>
    /// Contents with the single record <paramref name="type"/> = <paramref name="value"/>.
    /// </summary>
    public static OnionMessageContents Single(ulong type, ReadOnlyMemory<byte> value) =>
        new([new OnionMessageTlvRecord(type, value)]);
}