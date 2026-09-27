using System.Buffers.Binary;

namespace NLightning.Infrastructure.Bitcoin.Onion.OnionMessages;

using Domain.Channels.ValueObjects;
using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.OnionMessages;
using Domain.Protocol.OnionMessages.Constants;

/// <summary>
/// Writes and reads the <c>onionmsg_tlv</c> stream of one onion-message hop and the <c>blinded_path</c> of its
/// <c>reply_path</c> (BOLT 4 "Onion Messages"), for the packet builder and the unwrapper.
/// </summary>
/// <remarks>
/// <para>This project cannot reference Serialization, so hop payloads cross the Sphinx core as raw TLV bytes, as
/// with payment onions (<see cref="SphinxBigSize"/>).</para>
/// <para>Reader: canonical BigSize types and lengths, strictly increasing types, every length within the stream, no
/// unknown even type (2, 4, 64, 66 and 68 are the known ones), and a <c>reply_path</c> that is exactly one valid
/// <c>blinded_path</c>. Any failure returns <c>false</c> with a reason: BOLT 4 says such a message is ignored.</para>
/// </remarks>
internal static class OnionMessagePayloadCodec
{
    private const int SciddirLength = 1 + ShortChannelId.Length;

    /// <summary>
    /// The <c>onionmsg_tlv</c> of a non-final hop: only <c>encrypted_recipient_data</c> (BOLT 4 writer).
    /// </summary>
    public static byte[] EncodeNonFinal(ReadOnlyMemory<byte> encryptedRecipientData) =>
        Encode([(OnionMessageConstants.EncryptedRecipientDataType, encryptedRecipientData.ToArray())]);

    /// <summary>
    /// The <c>onionmsg_tlv</c> of the final hop: <c>reply_path</c> (if any), <c>encrypted_recipient_data</c> and the
    /// contents, in ascending type order.
    /// </summary>
    /// <exception cref="ArgumentException">The contents carry type 2 or 4, or a type twice.</exception>
    public static byte[] EncodeFinal(ReadOnlyMemory<byte> encryptedRecipientData, WireBlindedPath? replyPath,
                                     OnionMessageContents contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        var records = new List<(ulong Type, byte[] Value)>(contents.Records.Count + 2)
        {
            (OnionMessageConstants.EncryptedRecipientDataType, encryptedRecipientData.ToArray())
        };
        if (replyPath is not null)
            records.Add((OnionMessageConstants.ReplyPathType, EncodeBlindedPath(replyPath)));

        foreach (var record in contents.Records)
        {
            ArgumentNullException.ThrowIfNull(record, nameof(contents));
            if (record.Type is OnionMessageConstants.ReplyPathType
                            or OnionMessageConstants.EncryptedRecipientDataType)
                throw new ArgumentException(
                    $"onionmsg_tlv type {record.Type} is not part of the contents.", nameof(contents));
            records.Add((record.Type, record.Value.ToArray()));
        }

        records.Sort((a, b) => a.Type.CompareTo(b.Type));
        for (var i = 1; i < records.Count; i++)
        {
            if (records[i].Type == records[i - 1].Type)
                throw new ArgumentException($"onionmsg_tlv type {records[i].Type} appears twice.", nameof(contents));
        }

        return Encode(records);
    }

    /// <summary>
    /// Serializes a BOLT 4 <c>blinded_path</c>: <c>sciddir_or_pubkey first_node_id || point first_path_key ||
    /// byte num_hops || num_hops * (point blinded_node_id || u16 enclen || encrypted_recipient_data)</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The path has no hop, more than 255, or a hop's data exceeds 65535 bytes.
    /// </exception>
    public static byte[] EncodeBlindedPath(WireBlindedPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Hops.Count is 0 or > byte.MaxValue)
            throw new ArgumentException($"A blinded_path has 1 to 255 hops, got {path.Hops.Count}.", nameof(path));

        var firstNodeLength = path.FirstNode.IsNodeId ? CryptoConstants.CompactPubkeyLen : SciddirLength;
        var length = firstNodeLength + CryptoConstants.CompactPubkeyLen + 1;
        foreach (var hop in path.Hops)
        {
            if (hop.EncryptedRecipientData.Length > ushort.MaxValue)
                throw new ArgumentException("A blinded hop's encrypted_recipient_data exceeds 65535 bytes.",
                                            nameof(path));
            length += CryptoConstants.CompactPubkeyLen + 2 + hop.EncryptedRecipientData.Length;
        }

        var output = new byte[length];
        var position = 0;
        if (path.FirstNode.NodeId is { } nodeId)
        {
            ((byte[])nodeId).CopyTo(output, position);
        }
        else
        {
            output[position] = path.FirstNode.Direction;
            ((byte[])path.FirstNode.ShortChannelId!.Value).CopyTo(output, position + 1);
        }

        position += firstNodeLength;
        ((byte[])path.FirstPathKey).CopyTo(output, position);
        position += CryptoConstants.CompactPubkeyLen;
        output[position++] = (byte)path.Hops.Count;
        foreach (var hop in path.Hops)
        {
            ((byte[])hop.BlindedNodeId).CopyTo(output, position);
            position += CryptoConstants.CompactPubkeyLen;
            BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(position), (ushort)hop.EncryptedRecipientData.Length);
            position += 2;
            hop.EncryptedRecipientData.Span.CopyTo(output.AsSpan(position));
            position += hop.EncryptedRecipientData.Length;
        }

        return output;
    }

    /// <summary>
    /// Reads an <c>onionmsg_tlv</c> stream.
    /// </summary>
    /// <returns><c>false</c> with <paramref name="reason"/> when the stream is not a valid <c>onionmsg_tlv</c>.
    /// </returns>
    public static bool TryDecode(ReadOnlySpan<byte> stream, out OnionMessageTlvs? tlvs, out string? reason)
    {
        tlvs = null;
        WireBlindedPath? replyPath = null;
        ReadOnlyMemory<byte>? encryptedRecipientData = null;
        var others = new List<OnionMessageTlvRecord>();

        var position = 0;
        ulong? previousType = null;
        while (position < stream.Length)
        {
            if (!SphinxBigSize.TryRead(stream[position..], out var type, out var typeSize))
                return Fail("malformed onionmsg_tlv type", out reason);
            position += typeSize;

            if (previousType is { } previous && type <= previous)
                return Fail($"onionmsg_tlv type {type} is not in strictly increasing order", out reason);
            previousType = type;

            if (!SphinxBigSize.TryRead(stream[position..], out var length, out var lengthSize))
                return Fail($"malformed length of onionmsg_tlv type {type}", out reason);
            position += lengthSize;

            if (length > (ulong)(stream.Length - position))
                return Fail($"onionmsg_tlv type {type} is longer than the stream", out reason);

            var value = stream.Slice(position, (int)length);
            position += (int)length;

            switch (type)
            {
                case OnionMessageConstants.ReplyPathType:
                    if (!TryDecodeBlindedPath(value, out replyPath))
                        return Fail("reply_path is not a valid blinded_path", out reason);
                    break;
                case OnionMessageConstants.EncryptedRecipientDataType:
                    encryptedRecipientData = value.ToArray();
                    break;
                case OnionMessageConstants.InvoiceRequestType:
                case OnionMessageConstants.InvoiceType:
                case OnionMessageConstants.InvoiceErrorType:
                    others.Add(new OnionMessageTlvRecord(type, value.ToArray()));
                    break;
                default:
                    if (type % 2 == 0)
                        return Fail($"unknown even onionmsg_tlv type {type}", out reason);
                    others.Add(new OnionMessageTlvRecord(type, value.ToArray()));
                    break;
            }
        }

        tlvs = new OnionMessageTlvs(replyPath, encryptedRecipientData, others);
        reason = null;
        return true;
    }

    /// <summary>
    /// Reads a BOLT 4 <c>blinded_path</c> that fills <paramref name="value"/> exactly.
    /// </summary>
    public static bool TryDecodeBlindedPath(ReadOnlySpan<byte> value, out WireBlindedPath? path)
    {
        path = null;
        if (value.IsEmpty)
            return false;

        SciddirOrPubkey firstNode;
        int position;
        switch (value[0])
        {
            case 0 or 1:
                if (value.Length < SciddirLength)
                    return false;
                firstNode = SciddirOrPubkey.FromShortChannelId(new ShortChannelId(value[1..SciddirLength].ToArray()),
                                                               value[0]);
                position = SciddirLength;
                break;
            case 2 or 3:
                if (!TryReadPoint(value, 0, out var nodeId))
                    return false;
                firstNode = SciddirOrPubkey.FromNodeId(nodeId);
                position = CryptoConstants.CompactPubkeyLen;
                break;
            default:
                return false;
        }

        if (!TryReadPoint(value, position, out var firstPathKey))
            return false;
        position += CryptoConstants.CompactPubkeyLen;

        if (position >= value.Length)
            return false;
        var hopCount = value[position++];
        if (hopCount == 0)
            return false;

        var hops = new List<BlindedPathHop>(hopCount);
        for (var i = 0; i < hopCount; i++)
        {
            if (!TryReadPoint(value, position, out var blindedNodeId))
                return false;
            position += CryptoConstants.CompactPubkeyLen;

            if (value.Length - position < 2)
                return false;
            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(value[position..]);
            position += 2;
            if (value.Length - position < dataLength)
                return false;

            hops.Add(new BlindedPathHop(blindedNodeId, value.Slice(position, dataLength).ToArray()));
            position += dataLength;
        }

        if (position != value.Length)
            return false;

        path = new WireBlindedPath(firstNode, firstPathKey, hops);
        return true;
    }

    /// <summary>
    /// The size of one hop in <c>onionmsg_payloads</c>: its BigSize length, the payload and the 32-byte HMAC.
    /// </summary>
    public static int GetFramedLength(int payloadLength) =>
        SphinxBigSize.GetEncodedLength((ulong)payloadLength) + payloadLength + 32;

    private static byte[] Encode(IReadOnlyList<(ulong Type, byte[] Value)> records)
    {
        var length = records.Sum(r => SphinxBigSize.GetEncodedLength(r.Type)
                                    + SphinxBigSize.GetEncodedLength((ulong)r.Value.Length) + r.Value.Length);
        var output = new byte[length];
        var position = 0;
        foreach (var (type, value) in records)
        {
            position += SphinxBigSize.Write(type, output.AsSpan(position));
            position += SphinxBigSize.Write((ulong)value.Length, output.AsSpan(position));
            value.CopyTo(output, position);
            position += value.Length;
        }

        return output;
    }

    private static bool TryReadPoint(ReadOnlySpan<byte> value, int position, out CompactPubKey point)
    {
        point = default;
        if (value.Length - position < CryptoConstants.CompactPubkeyLen)
            return false;

        var bytes = value.Slice(position, CryptoConstants.CompactPubkeyLen);
        if (!SphinxKeyGenerator.IsValidPublicKey(bytes))
            return false;

        point = new CompactPubKey(bytes.ToArray());
        return true;
    }

    private static bool Fail(string message, out string reason)
    {
        reason = message;
        return false;
    }
}