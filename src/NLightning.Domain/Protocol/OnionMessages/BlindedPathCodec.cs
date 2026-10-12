using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Protocol.OnionMessages;

using Crypto.Constants;
using Crypto.ValueObjects;
using Onion.Models;

/// <summary>
/// The BOLT 4 <c>blinded_path</c> wire codec:
/// <c>sciddir_or_pubkey first_node_id || point first_path_key || byte num_hops ||
/// num_hops * (point blinded_node_id || u16 enclen || enclen*byte encrypted_recipient_data)</c>.
/// </summary>
/// <remarks>
/// <para>Used for the <c>onionmsg_tlv</c> <c>reply_path</c> and, in BOLT 12, for the <c>blinded_path*</c> lists
/// (<see cref="TryReadList"/>). A path with no hop, a hop whose <c>enclen</c> runs past the end, a point without a
/// 02/03 prefix or an undefined <c>sciddir_or_pubkey</c> prefix is refused. Points are not checked against the curve
/// here (the Domain has no secp256k1): an off-curve key fails when it is used.</para>
/// <para>The codec also maps to and from M5's <see cref="BlindedPath"/>, whose introduction node is a node id: a path
/// whose <c>first_node_id</c> is a SCID and direction needs a resolved node id first.</para>
/// </remarks>
public static class BlindedPathCodec
{
    private const int HopHeaderLength = CryptoConstants.CompactPubkeyLen + sizeof(ushort);

    /// <summary>
    /// The encoded length of <paramref name="path"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The path cannot be encoded (see <see cref="Encode"/>).</exception>
    public static int GetLength(WireBlindedPath path)
    {
        Validate(path);

        var length = SciddirOrPubkeyCodec.GetLength(path.FirstNode) + CryptoConstants.CompactPubkeyLen + 1;
        foreach (var hop in path.Hops)
            length += HopHeaderLength + hop.EncryptedRecipientData.Length;

        return length;
    }

    /// <summary>
    /// Writes <paramref name="path"/> at the start of <paramref name="destination"/>.
    /// </summary>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="ArgumentException">The path cannot be encoded, or <paramref name="destination"/> is too
    /// short.</exception>
    public static int Write(WireBlindedPath path, Span<byte> destination)
    {
        var length = GetLength(path);
        if (destination.Length < length)
            throw new ArgumentException($"Destination needs {length} bytes, got {destination.Length}.",
                                        nameof(destination));

        var offset = SciddirOrPubkeyCodec.Write(path.FirstNode, destination);
        ((ReadOnlySpan<byte>)path.FirstPathKey).CopyTo(destination[offset..]);
        offset += CryptoConstants.CompactPubkeyLen;
        destination[offset++] = (byte)path.Hops.Count;

        foreach (var hop in path.Hops)
        {
            ((ReadOnlySpan<byte>)hop.BlindedNodeId).CopyTo(destination[offset..]);
            offset += CryptoConstants.CompactPubkeyLen;
            BinaryPrimitives.WriteUInt16BigEndian(destination[offset..], (ushort)hop.EncryptedRecipientData.Length);
            offset += sizeof(ushort);
            hop.EncryptedRecipientData.Span.CopyTo(destination[offset..]);
            offset += hop.EncryptedRecipientData.Length;
        }

        return offset;
    }

    /// <summary>
    /// Encodes <paramref name="path"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The path has no hop or more than 255, or a hop's
    /// <c>encrypted_recipient_data</c> is longer than 65535 bytes.</exception>
    public static byte[] Encode(WireBlindedPath path)
    {
        var bytes = new byte[GetLength(path)];
        Write(path, bytes);
        return bytes;
    }

    /// <summary>
    /// Encodes <paramref name="paths"/> back to back (a BOLT 12 <c>blinded_path*</c> value).
    /// </summary>
    /// <exception cref="ArgumentException">A path cannot be encoded.</exception>
    public static byte[] EncodeList(IReadOnlyList<WireBlindedPath> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var bytes = new byte[paths.Sum(GetLength)];
        var offset = 0;
        foreach (var path in paths)
            offset += Write(path, bytes.AsSpan(offset));

        return bytes;
    }

    /// <summary>
    /// Reads one <c>blinded_path</c> from the start of <paramref name="data"/> (trailing bytes are left for the
    /// caller).
    /// </summary>
    /// <param name="data">The bytes to read from.</param>
    /// <param name="path">The decoded path.</param>
    /// <param name="bytesRead">How many bytes it took, 0 on failure.</param>
    /// <param name="reason">Why the bytes are refused.</param>
    public static bool TryRead(ReadOnlySpan<byte> data, [NotNullWhen(true)] out WireBlindedPath? path,
                               out int bytesRead, [NotNullWhen(false)] out string? reason)
    {
        path = null;
        bytesRead = 0;

        if (!SciddirOrPubkeyCodec.TryRead(data, out var firstNode, out var offset, out var firstNodeReason))
        {
            reason = $"blinded_path first_node_id: {firstNodeReason}";
            return false;
        }

        if (!TryReadPoint(data, ref offset, "first_path_key", out var firstPathKey, out reason))
            return false;

        if (data.Length - offset < 1)
        {
            reason = "blinded_path is truncated before num_hops.";
            return false;
        }

        var numHops = data[offset++];
        if (numHops == 0)
        {
            reason = "blinded_path has num_hops 0.";
            return false;
        }

        var hops = new List<BlindedPathHop>(numHops);
        for (var i = 0; i < numHops; i++)
        {
            if (!TryReadPoint(data, ref offset, $"hop {i} blinded_node_id", out var blindedNodeId, out reason))
                return false;

            if (data.Length - offset < sizeof(ushort))
            {
                reason = $"blinded_path hop {i} is truncated before enclen.";
                return false;
            }

            var encryptedLength = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            offset += sizeof(ushort);
            if (data.Length - offset < encryptedLength)
            {
                reason = $"blinded_path hop {i} enclen {encryptedLength} runs past the end "
                       + $"({data.Length - offset} bytes left).";
                return false;
            }

            hops.Add(new BlindedPathHop(blindedNodeId, data.Slice(offset, encryptedLength).ToArray()));
            offset += encryptedLength;
        }

        path = new WireBlindedPath(firstNode, firstPathKey, hops);
        bytesRead = offset;
        reason = null;
        return true;
    }

    /// <summary>
    /// Decodes a <c>blinded_path</c> that fills <paramref name="data"/> exactly (an <c>onionmsg_tlv</c>
    /// <c>reply_path</c>).
    /// </summary>
    /// <param name="data">The bytes.</param>
    /// <param name="path">The decoded path.</param>
    /// <param name="reason">Why the bytes are refused, including trailing bytes after the path.</param>
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out WireBlindedPath? path,
                                 [NotNullWhen(false)] out string? reason)
    {
        if (!TryRead(data, out path, out var bytesRead, out reason))
            return false;

        if (bytesRead == data.Length)
            return true;

        reason = $"blinded_path is {bytesRead} bytes, but {data.Length - bytesRead} bytes follow it.";
        path = null;
        return false;
    }

    /// <summary>
    /// Decodes a <c>blinded_path</c> that fills <paramref name="data"/> exactly.
    /// </summary>
    /// <exception cref="FormatException">The bytes are not exactly one valid <c>blinded_path</c>.</exception>
    public static WireBlindedPath Decode(ReadOnlySpan<byte> data)
    {
        return TryDecode(data, out var path, out var reason) ? path : throw new FormatException(reason);
    }

    /// <summary>
    /// Decodes a BOLT 12 <c>blinded_path*</c> value: paths back to back until the end of <paramref name="data"/>.
    /// </summary>
    /// <param name="data">The bytes (empty means no path).</param>
    /// <param name="paths">The decoded paths.</param>
    /// <param name="reason">Why the bytes are refused (the first bad path's reason, with its index).</param>
    public static bool TryReadList(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IReadOnlyList<WireBlindedPath>? paths,
                                   [NotNullWhen(false)] out string? reason)
    {
        paths = null;
        var list = new List<WireBlindedPath>();
        var offset = 0;

        while (offset < data.Length)
        {
            if (!TryRead(data[offset..], out var path, out var bytesRead, out var pathReason))
            {
                reason = $"blinded_path {list.Count}: {pathReason}";
                return false;
            }

            list.Add(path);
            offset += bytesRead;
        }

        paths = list;
        reason = null;
        return true;
    }

    /// <summary>
    /// The M5 <see cref="BlindedPath"/> of a wire path whose <c>first_node_id</c> is a node id.
    /// </summary>
    /// <param name="path">The wire path.</param>
    /// <param name="blindedPath">The M5 path, or null when the introduction node is a SCID and direction.</param>
    public static bool TryToBlindedPath(WireBlindedPath path, [NotNullWhen(true)] out BlindedPath? blindedPath)
    {
        ArgumentNullException.ThrowIfNull(path);

        blindedPath = path.FirstNode.NodeId is { } nodeId
                          ? new BlindedPath(nodeId, path.FirstPathKey, path.Hops)
                          : null;
        return blindedPath is not null;
    }

    /// <summary>
    /// The M5 <see cref="BlindedPath"/> of a wire path, with its introduction node resolved to
    /// <paramref name="firstNodeId"/> (from our channels or the graph when <c>first_node_id</c> is a SCID and
    /// direction).
    /// </summary>
    /// <exception cref="ArgumentException"><c>first_node_id</c> is a node id other than
    /// <paramref name="firstNodeId"/>.</exception>
    public static BlindedPath ToBlindedPath(WireBlindedPath path, CompactPubKey firstNodeId)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (path.FirstNode.NodeId is { } nodeId && nodeId != firstNodeId)
            throw new ArgumentException("The path's first_node_id is another node id.", nameof(firstNodeId));

        return new BlindedPath(firstNodeId, path.FirstPathKey, path.Hops);
    }

    private static void Validate(WireBlindedPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(path.FirstNode);
        ArgumentNullException.ThrowIfNull(path.Hops);

        if (path.Hops.Count is 0 or > byte.MaxValue)
            throw new ArgumentException($"A blinded_path has 1 to {byte.MaxValue} hops, got {path.Hops.Count}.",
                                        nameof(path));

        for (var i = 0; i < path.Hops.Count; i++)
        {
            if (path.Hops[i].EncryptedRecipientData.Length > ushort.MaxValue)
                throw new ArgumentException(
                    $"Hop {i} encrypted_recipient_data is {path.Hops[i].EncryptedRecipientData.Length} bytes, more "
                  + $"than enclen can carry ({ushort.MaxValue}).", nameof(path));
        }
    }

    private static bool TryReadPoint(ReadOnlySpan<byte> data, ref int offset, string field, out CompactPubKey point,
                                     [NotNullWhen(false)] out string? reason)
    {
        point = default;

        if (data.Length - offset < CryptoConstants.CompactPubkeyLen)
        {
            reason = $"blinded_path is truncated in {field}.";
            return false;
        }

        var bytes = data.Slice(offset, CryptoConstants.CompactPubkeyLen);
        if (bytes[0] is not (0x02 or 0x03))
        {
            reason = $"blinded_path {field} starts with 0x{bytes[0]:x2}, not a compressed point.";
            return false;
        }

        point = new CompactPubKey(bytes.ToArray());
        offset += CryptoConstants.CompactPubkeyLen;
        reason = null;
        return true;
    }
}