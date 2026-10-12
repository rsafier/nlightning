using System.Buffers.Binary;
using System.Text;

namespace NLightning.Tests.Utils.Bolt12;

using Domain.Protocol.OnionMessages;

/// <summary>
/// A test-only BOLT 12 offer encoder for the onion-message proofs (plan Proof M6 (c)): enough of an <c>lno1...</c>
/// string for Core Lightning's <c>fetchinvoice</c> to send an <c>invoice_request</c> to one of our nodes. It writes
/// <c>offer_chains</c> (2), <c>offer_description</c> (10), <c>offer_paths</c> (16) and <c>offer_issuer_id</c> (22) in
/// ascending type order and the BOLT 12 string encoding (bech32 5-bit words without a checksum, hrp <c>lno</c>).
/// </summary>
/// <remarks>
/// Nothing is validated beyond the shapes: lane B12-A's <c>Bolt12Bech32</c> and <c>Offer</c> replace it. BOLT 12
/// requires <c>offer_issuer_id</c> or <c>offer_paths</c> (or both); without <c>offer_chains</c> the offer is for
/// bitcoin mainnet, which a regtest CLN refuses.
/// </remarks>
public static class MinimalOfferEncoder
{
    /// <summary>
    /// The human-readable part of an offer.
    /// </summary>
    public const string OfferHrp = "lno";

    public const ulong OfferChainsType = 2;
    public const ulong OfferDescriptionType = 10;
    public const ulong OfferPathsType = 16;
    public const ulong OfferIssuerIdType = 22;

    private const string Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";

    /// <summary>
    /// Encodes an offer as its <c>lno1...</c> string.
    /// </summary>
    /// <param name="chainHash">The one <c>offer_chains</c> entry (32 bytes, as on the wire), or null for none.</param>
    /// <param name="description">The <c>offer_description</c>, or null for none.</param>
    /// <param name="issuerId">The <c>offer_issuer_id</c> (33 bytes), or null for none.</param>
    /// <param name="paths">The <c>offer_paths</c>, or null for none.</param>
    public static string Encode(ReadOnlyMemory<byte>? chainHash, string? description, ReadOnlyMemory<byte>? issuerId,
                                IReadOnlyList<WireBlindedPath>? paths = null) =>
        ToBolt12String(OfferHrp, EncodeTlvs(chainHash, description, issuerId, paths));

    /// <summary>
    /// The offer's TLV stream (the bytes the <c>lno1...</c> string carries).
    /// </summary>
    /// <exception cref="ArgumentException">Neither an issuer id nor a path, or a key of the wrong length.</exception>
    public static byte[] EncodeTlvs(ReadOnlyMemory<byte>? chainHash, string? description,
                                    ReadOnlyMemory<byte>? issuerId, IReadOnlyList<WireBlindedPath>? paths = null)
    {
        if (issuerId is null && (paths is null || paths.Count == 0))
            throw new ArgumentException("A BOLT 12 offer needs offer_issuer_id or offer_paths");
        if (chainHash is { Length: not 32 })
            throw new ArgumentException("A chain hash is 32 bytes", nameof(chainHash));
        if (issuerId is { Length: not 33 })
            throw new ArgumentException("offer_issuer_id is a 33-byte point", nameof(issuerId));

        using var stream = new MemoryStream();
        if (chainHash is { } chain)
            WriteRecord(stream, OfferChainsType, chain.Span);
        if (description is not null)
            WriteRecord(stream, OfferDescriptionType, Encoding.UTF8.GetBytes(description));
        if (paths is { Count: > 0 })
        {
            using var pathBytes = new MemoryStream();
            foreach (var path in paths)
                pathBytes.Write(EncodeBlindedPath(path));
            WriteRecord(stream, OfferPathsType, pathBytes.ToArray());
        }

        if (issuerId is { } issuer)
            WriteRecord(stream, OfferIssuerIdType, issuer.Span);

        return stream.ToArray();
    }

    /// <summary>
    /// A BOLT 4 <c>blinded_path</c>: <c>sciddir_or_pubkey first_node_id</c>, <c>point first_path_key</c>,
    /// <c>byte num_hops</c>, then per hop <c>point blinded_node_id</c>, <c>u16 enclen</c> and the
    /// <c>encrypted_recipient_data</c>.
    /// </summary>
    /// <exception cref="ArgumentException">No hop, more than 255 hops, or a hop's data longer than 65535 bytes.
    /// </exception>
    public static byte[] EncodeBlindedPath(WireBlindedPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Hops.Count is 0 or > byte.MaxValue)
            throw new ArgumentException("A blinded_path has 1 to 255 hops", nameof(path));

        using var stream = new MemoryStream();
        if (path.FirstNode.NodeId is { } nodeId)
        {
            stream.Write((byte[])nodeId);
        }
        else
        {
            stream.WriteByte(path.FirstNode.Direction);
            stream.Write((byte[])path.FirstNode.ShortChannelId!.Value);
        }

        stream.Write((byte[])path.FirstPathKey);
        stream.WriteByte((byte)path.Hops.Count);
        Span<byte> length = stackalloc byte[2];
        foreach (var hop in path.Hops)
        {
            if (hop.EncryptedRecipientData.Length > ushort.MaxValue)
                throw new ArgumentException("encrypted_recipient_data is at most 65535 bytes", nameof(path));

            stream.Write((byte[])hop.BlindedNodeId);
            BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)hop.EncryptedRecipientData.Length);
            stream.Write(length);
            stream.Write(hop.EncryptedRecipientData.Span);
        }

        return stream.ToArray();
    }

    /// <summary>
    /// The BOLT 12 string form: <paramref name="hrp"/>, <c>1</c>, then the bytes as bech32 5-bit words (zero-padded),
    /// lower case and without a checksum.
    /// </summary>
    public static string ToBolt12String(string hrp, ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder(hrp.Length + 1 + (data.Length * 8 + 4) / 5);
        builder.Append(hrp).Append('1');
        int accumulator = 0, bits = 0;
        foreach (var b in data)
        {
            accumulator = (accumulator << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                builder.Append(Charset[(accumulator >> bits) & 31]);
            }
        }

        if (bits > 0)
            builder.Append(Charset[(accumulator << (5 - bits)) & 31]);

        return builder.ToString();
    }

    /// <summary>
    /// One TLV record: <c>bigsize type</c>, <c>bigsize length</c>, value.
    /// </summary>
    public static void WriteRecord(Stream stream, ulong type, ReadOnlySpan<byte> value)
    {
        WriteBigSize(stream, type);
        WriteBigSize(stream, (ulong)value.Length);
        stream.Write(value);
    }

    /// <summary>
    /// A BOLT 1 <c>bigsize</c> (minimal, big-endian).
    /// </summary>
    public static void WriteBigSize(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[9];
        switch (value)
        {
            case < 0xfd:
                stream.WriteByte((byte)value);
                break;
            case <= ushort.MaxValue:
                buffer[0] = 0xfd;
                BinaryPrimitives.WriteUInt16BigEndian(buffer[1..], (ushort)value);
                stream.Write(buffer[..3]);
                break;
            case <= uint.MaxValue:
                buffer[0] = 0xfe;
                BinaryPrimitives.WriteUInt32BigEndian(buffer[1..], (uint)value);
                stream.Write(buffer[..5]);
                break;
            default:
                buffer[0] = 0xff;
                BinaryPrimitives.WriteUInt64BigEndian(buffer[1..], value);
                stream.Write(buffer);
                break;
        }
    }
}