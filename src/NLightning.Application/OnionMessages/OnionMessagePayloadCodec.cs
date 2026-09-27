using System.Buffers.Binary;

namespace NLightning.Application.OnionMessages;

using Domain.Channels.ValueObjects;
using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.OnionMessages;
using Domain.Protocol.OnionMessages.Constants;
using Domain.Protocol.Tlv;

/// <summary>
/// The strict <c>onionmsg_tlv</c> and <c>blinded_path</c> codec the service reads final and non-final hop payloads
/// with (BOLT 4 "Onion Messages", BOLT 1 TLV rules): strictly increasing types, minimal bigsizes, lengths inside the
/// stream, unknown even types rejected and unknown odd types kept verbatim.
/// </summary>
/// <remarks>
/// Lane M6-A ships the Domain codecs (<c>OnionMessageTlvsCodec</c>, <c>BlindedPathCodec</c>); this lane coded against
/// the contracts only, so it carries its own copy. At integration the bodies here can call those codecs.
/// </remarks>
internal static class OnionMessagePayloadCodec
{
    private const int ScidOrPubkeyScidLength = 9;
    private const int ShortChannelIdLength = 8;

    /// <summary>
    /// Decodes one hop's <c>onionmsg_tlv</c>.
    /// </summary>
    /// <returns>False when the stream is not a valid <c>onionmsg_tlv</c> (the reader ignores the message).</returns>
    public static bool TryDecode(ReadOnlySpan<byte> payload, out OnionMessageTlvs? tlvs)
    {
        tlvs = null;
        WireBlindedPath? replyPath = null;
        ReadOnlyMemory<byte>? encryptedRecipientData = null;
        var other = new List<OnionMessageTlvRecord>();
        ulong? previousType = null;
        var offset = 0;
        while (offset < payload.Length)
        {
            if (!BigSizeCodec.TryRead(payload[offset..], out var type, out var typeLength))
                return false;
            offset += typeLength;
            if (!BigSizeCodec.TryRead(payload[offset..], out var length, out var lengthLength))
                return false;
            offset += lengthLength;
            if (length > (ulong)(payload.Length - offset))
                return false;
            if (previousType is { } previous && type <= previous)
                return false;
            previousType = type;

            var value = payload.Slice(offset, (int)length);
            offset += (int)length;
            switch (type)
            {
                case OnionMessageConstants.ReplyPathType:
                    if (!TryDecodeBlindedPath(value, out var path, out var consumed) || consumed != value.Length)
                        return false;
                    replyPath = path;
                    break;
                case OnionMessageConstants.EncryptedRecipientDataType:
                    encryptedRecipientData = value.ToArray();
                    break;
                case OnionMessageConstants.InvoiceRequestType:
                case OnionMessageConstants.InvoiceType:
                case OnionMessageConstants.InvoiceErrorType:
                    other.Add(new OnionMessageTlvRecord(type, value.ToArray()));
                    break;
                default:
                    // Unknown even types make the stream invalid (BOLT 1); unknown odd ones are kept
                    if (type % 2 == 0)
                        return false;
                    other.Add(new OnionMessageTlvRecord(type, value.ToArray()));
                    break;
            }
        }

        tlvs = new OnionMessageTlvs(replyPath, encryptedRecipientData, other);
        return true;
    }

    /// <summary>
    /// Encodes an <c>onionmsg_tlv</c>, records in ascending type order.
    /// </summary>
    /// <exception cref="ArgumentException">Two records share a type, or a record uses type 2 or 4.</exception>
    public static byte[] Encode(OnionMessageTlvs tlvs)
    {
        ArgumentNullException.ThrowIfNull(tlvs);
        var records = new List<(ulong Type, byte[] Value)>();
        if (tlvs.ReplyPath is { } replyPath)
            records.Add((OnionMessageConstants.ReplyPathType, EncodeBlindedPath(replyPath)));
        if (tlvs.EncryptedRecipientData is { } data)
            records.Add((OnionMessageConstants.EncryptedRecipientDataType, data.ToArray()));
        foreach (var record in tlvs.OtherRecords)
        {
            if (record.Type is OnionMessageConstants.ReplyPathType or OnionMessageConstants.EncryptedRecipientDataType)
                throw new ArgumentException($"Type {record.Type} is not an other record", nameof(tlvs));
            records.Add((record.Type, record.Value.ToArray()));
        }

        records.Sort((a, b) => a.Type.CompareTo(b.Type));
        for (var i = 1; i < records.Count; i++)
            if (records[i].Type == records[i - 1].Type)
                throw new ArgumentException($"Type {records[i].Type} appears twice", nameof(tlvs));

        var length = records.Sum(r => BigSizeCodec.GetLength(r.Type) + BigSizeCodec.GetLength((ulong)r.Value.Length)
                                    + r.Value.Length);
        var buffer = new byte[length];
        var offset = 0;
        foreach (var (type, value) in records)
        {
            offset += BigSizeCodec.Write(type, buffer.AsSpan(offset));
            offset += BigSizeCodec.Write((ulong)value.Length, buffer.AsSpan(offset));
            value.CopyTo(buffer.AsSpan(offset));
            offset += value.Length;
        }

        return buffer;
    }

    /// <summary>
    /// Decodes a <c>blinded_path</c> from the start of <paramref name="data"/>.
    /// </summary>
    /// <returns>False when it is truncated, has no hops, or has an invalid <c>sciddir_or_pubkey</c> or point.</returns>
    public static bool TryDecodeBlindedPath(ReadOnlySpan<byte> data, out WireBlindedPath? path, out int consumed)
    {
        path = null;
        consumed = 0;
        if (data.IsEmpty)
            return false;

        SciddirOrPubkey firstNode;
        var offset = 0;
        switch (data[0])
        {
            case 0 or 1:
                if (data.Length < ScidOrPubkeyScidLength)
                    return false;
                firstNode = SciddirOrPubkey.FromShortChannelId(
                    new ShortChannelId(data.Slice(1, ShortChannelIdLength).ToArray()), data[0]);
                offset = ScidOrPubkeyScidLength;
                break;
            case 2 or 3:
                if (!TryReadPoint(data, ref offset, out var nodeId))
                    return false;
                firstNode = SciddirOrPubkey.FromNodeId(nodeId);
                break;
            default:
                return false;
        }

        if (!TryReadPoint(data, ref offset, out var firstPathKey) || offset >= data.Length)
            return false;

        var numHops = data[offset++];
        if (numHops == 0)
            return false;

        var hops = new List<BlindedPathHop>(numHops);
        for (var i = 0; i < numHops; i++)
        {
            if (!TryReadPoint(data, ref offset, out var blindedNodeId) || data.Length - offset < 2)
                return false;
            var encryptedLength = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            offset += 2;
            if (data.Length - offset < encryptedLength)
                return false;
            hops.Add(new BlindedPathHop(blindedNodeId, data.Slice(offset, encryptedLength).ToArray()));
            offset += encryptedLength;
        }

        path = new WireBlindedPath(firstNode, firstPathKey, hops);
        consumed = offset;
        return true;
    }

    /// <summary>
    /// Encodes a <c>blinded_path</c>.
    /// </summary>
    /// <exception cref="ArgumentException">It has no hops or more than 255, or a hop's data exceeds 65535 bytes.
    /// </exception>
    public static byte[] EncodeBlindedPath(WireBlindedPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Hops.Count is 0 or > byte.MaxValue)
            throw new ArgumentException("A blinded path has 1 to 255 hops", nameof(path));

        using var stream = new MemoryStream();
        if (path.FirstNode.NodeId is { } nodeId)
        {
            stream.Write(nodeId);
        }
        else
        {
            stream.WriteByte(path.FirstNode.Direction);
            stream.Write((byte[])path.FirstNode.ShortChannelId!.Value);
        }

        stream.Write(path.FirstPathKey);
        stream.WriteByte((byte)path.Hops.Count);
        Span<byte> length = stackalloc byte[2];
        foreach (var hop in path.Hops)
        {
            if (hop.EncryptedRecipientData.Length > ushort.MaxValue)
                throw new ArgumentException("A hop's encrypted_recipient_data exceeds 65535 bytes", nameof(path));
            stream.Write(hop.BlindedNodeId);
            BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)hop.EncryptedRecipientData.Length);
            stream.Write(length);
            stream.Write(hop.EncryptedRecipientData.Span);
        }

        return stream.ToArray();
    }

    /// <summary>
    /// The number of final-hop payload fields (types 64 and up, BOLT 4) in <paramref name="records"/>.
    /// </summary>
    public static int CountPayloadFields(IEnumerable<OnionMessageTlvRecord> records) =>
        records.Count(r => r.Type >= OnionMessageConstants.FirstPayloadFieldType);

    private static bool TryReadPoint(ReadOnlySpan<byte> data, ref int offset, out CompactPubKey point)
    {
        point = default;
        if (data.Length - offset < CryptoConstants.CompactPubkeyLen)
            return false;
        var prefix = data[offset];
        if (prefix is not (2 or 3))
            return false;
        point = new CompactPubKey(data.Slice(offset, CryptoConstants.CompactPubkeyLen).ToArray());
        offset += CryptoConstants.CompactPubkeyLen;
        return true;
    }
}