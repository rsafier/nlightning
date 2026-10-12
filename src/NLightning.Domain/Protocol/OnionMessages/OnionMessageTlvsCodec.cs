using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Protocol.OnionMessages;

using Constants;
using Tlv;

/// <summary>
/// The strict <c>onionmsg_tlv</c> codec (BOLT 4 "Onion Messages"): the TLV stream one onion-message hop carries.
/// </summary>
/// <remarks>
/// <para>Reading follows BOLT 1 and the BOLT 4 reader: types strictly increasing, type and length minimally encoded
/// (canonical bigsize), a length never past the end, a <c>reply_path</c> (2) that is exactly one valid
/// <c>blinded_path</c>. Known types are 2, 4, 64, 66 and 68; an unknown even type is refused (the message is
/// ignored), an unknown odd type is kept verbatim in <see cref="OnionMessageTlvs.OtherRecords"/>.</para>
/// <para>Writing sorts every record by type. It never checks the BOLT 4 writer rules about which hop may carry what
/// (a non-final hop carries only <c>encrypted_recipient_data</c>): that is the packet builder's job.</para>
/// </remarks>
public static class OnionMessageTlvsCodec
{
    /// <summary>
    /// Decodes an <c>onionmsg_tlv</c> stream that fills <paramref name="data"/>.
    /// </summary>
    /// <param name="data">The stream (empty is a valid, empty payload).</param>
    /// <param name="tlvs">The decoded records.</param>
    /// <param name="reason">Why the stream is refused; BOLT 4 says to ignore such a message.</param>
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out OnionMessageTlvs? tlvs,
                                 [NotNullWhen(false)] out string? reason)
    {
        tlvs = null;

        WireBlindedPath? replyPath = null;
        ReadOnlyMemory<byte>? encryptedRecipientData = null;
        var others = new List<OnionMessageTlvRecord>();
        ulong? previousType = null;
        var offset = 0;

        while (offset < data.Length)
        {
            if (!BigSizeCodec.TryRead(data[offset..], out var type, out var typeLength))
            {
                reason = $"onionmsg_tlv type at offset {offset} is truncated or not minimally encoded.";
                return false;
            }

            if (previousType is { } previous && type <= previous)
            {
                reason = $"onionmsg_tlv type {type} at offset {offset} does not follow type {previous} in "
                       + "increasing order.";
                return false;
            }

            var lengthOffset = offset + typeLength;
            if (!BigSizeCodec.TryRead(data[lengthOffset..], out var length, out var lengthLength))
            {
                reason = $"onionmsg_tlv length of type {type} is truncated or not minimally encoded.";
                return false;
            }

            var valueOffset = lengthOffset + lengthLength;
            if (length > (ulong)(data.Length - valueOffset))
            {
                reason = $"onionmsg_tlv type {type} has length {length}, but only {data.Length - valueOffset} bytes "
                       + "are left.";
                return false;
            }

            var value = data.Slice(valueOffset, (int)length);
            switch (type)
            {
                case OnionMessageConstants.ReplyPathType:
                    if (!BlindedPathCodec.TryDecode(value, out replyPath, out var pathReason))
                    {
                        reason = $"onionmsg_tlv reply_path: {pathReason}";
                        return false;
                    }

                    break;
                case OnionMessageConstants.EncryptedRecipientDataType:
                    encryptedRecipientData = value.ToArray();
                    break;
                case OnionMessageConstants.InvoiceRequestType or OnionMessageConstants.InvoiceType
                                                               or OnionMessageConstants.InvoiceErrorType:
                    others.Add(new OnionMessageTlvRecord(type, value.ToArray()));
                    break;
                default:
                    if (type % 2 == 0)
                    {
                        reason = $"onionmsg_tlv has unknown even type {type}.";
                        return false;
                    }

                    others.Add(new OnionMessageTlvRecord(type, value.ToArray()));
                    break;
            }

            previousType = type;
            offset = valueOffset + (int)length;
        }

        tlvs = new OnionMessageTlvs(replyPath, encryptedRecipientData, others);
        reason = null;
        return true;
    }

    /// <summary>
    /// Decodes an <c>onionmsg_tlv</c> stream that fills <paramref name="data"/>.
    /// </summary>
    /// <exception cref="FormatException">The stream is refused (see <see cref="TryDecode"/>).</exception>
    public static OnionMessageTlvs Decode(ReadOnlySpan<byte> data)
    {
        return TryDecode(data, out var tlvs, out var reason) ? tlvs : throw new FormatException(reason);
    }

    /// <summary>
    /// Encodes <paramref name="tlvs"/>: <c>reply_path</c>, <c>encrypted_recipient_data</c> and every other record,
    /// in increasing type order.
    /// </summary>
    /// <exception cref="ArgumentException"><see cref="OnionMessageTlvs.OtherRecords"/> holds type 2 or 4 or a
    /// type twice, or the reply path cannot be encoded.</exception>
    public static byte[] Encode(OnionMessageTlvs tlvs)
    {
        ArgumentNullException.ThrowIfNull(tlvs);
        ArgumentNullException.ThrowIfNull(tlvs.OtherRecords);

        var records = new SortedDictionary<ulong, ReadOnlyMemory<byte>>();
        if (tlvs.ReplyPath is { } replyPath)
            records.Add(OnionMessageConstants.ReplyPathType, BlindedPathCodec.Encode(replyPath));
        if (tlvs.EncryptedRecipientData is { } encryptedRecipientData)
            records.Add(OnionMessageConstants.EncryptedRecipientDataType, encryptedRecipientData);

        foreach (var record in tlvs.OtherRecords)
        {
            if (record.Type is OnionMessageConstants.ReplyPathType or OnionMessageConstants.EncryptedRecipientDataType)
                throw new ArgumentException(
                    $"Type {record.Type} goes in its own OnionMessageTlvs property, not in OtherRecords.",
                    nameof(tlvs));
            if (!records.TryAdd(record.Type, record.Value))
                throw new ArgumentException($"onionmsg_tlv type {record.Type} appears twice.", nameof(tlvs));
        }

        var length = 0;
        foreach (var (type, value) in records)
            length += BigSizeCodec.GetLength(type) + BigSizeCodec.GetLength((ulong)value.Length) + value.Length;

        var bytes = new byte[length];
        var offset = 0;
        foreach (var (type, value) in records)
        {
            offset += BigSizeCodec.Write(type, bytes.AsSpan(offset));
            offset += BigSizeCodec.Write((ulong)value.Length, bytes.AsSpan(offset));
            value.Span.CopyTo(bytes.AsSpan(offset));
            offset += value.Length;
        }

        return bytes;
    }

    /// <summary>
    /// How many final-hop payload fields (types from 64 up, BOLT 4: "Field numbers 64 and above are reserved for
    /// payloads for the final hop") <paramref name="tlvs"/> carries. A final hop with more than one is ignored
    /// (OM-R-07).
    /// </summary>
    public static int CountPayloadFields(OnionMessageTlvs tlvs)
    {
        ArgumentNullException.ThrowIfNull(tlvs);
        return CountPayloadFields(tlvs.OtherRecords);
    }

    /// <summary>
    /// How many records of <paramref name="records"/> are final-hop payload fields (types from 64 up).
    /// </summary>
    public static int CountPayloadFields(IEnumerable<OnionMessageTlvRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        return records.Count(r => r.Type >= OnionMessageConstants.FirstPayloadFieldType);
    }

    /// <summary>
    /// The final hop's contents: every record other than <c>reply_path</c> and <c>encrypted_recipient_data</c>.
    /// </summary>
    public static OnionMessageContents ToContents(OnionMessageTlvs tlvs)
    {
        ArgumentNullException.ThrowIfNull(tlvs);
        return new OnionMessageContents(tlvs.OtherRecords);
    }
}