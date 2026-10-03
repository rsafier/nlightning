using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace NLightning.Domain.Offers.Encoding;

using Crypto.ValueObjects;
using Models;
using Protocol.Onion.Codecs;
using Protocol.Onion.Models;
using Protocol.OnionMessages;
using Protocol.Tlv;
using Protocol.ValueObjects;
using Validators;

/// <summary>
/// Value codecs of the BOLT 12 field types (<c>tu64</c>, <c>tu32</c>, <c>utf8</c>, <c>point</c>, <c>chain_hash</c>,
/// <c>blinded_path</c>, <c>blinded_payinfo</c>, <c>fallback_address</c>, <c>bip340sig</c>): strict readers and the
/// writers <see cref="Bolt12TlvStreamBuilder"/> uses.
/// </summary>
public static class Bolt12FieldCodec
{
    /// <summary>
    /// The encoded length of one <c>blinded_payinfo</c> without its features.
    /// </summary>
    public const int PayInfoFixedLength = BlindedPayInfoCodec.FixedLength;

    private const int ChainHashLength = 32;
    private const int PointLength = 33;

    private static readonly UTF8Encoding s_strictUtf8 = new(false, true);

    /// <summary>
    /// Encodes <paramref name="text"/> as UTF-8.
    /// </summary>
    public static byte[] EncodeUtf8(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return s_strictUtf8.GetBytes(text);
    }

    /// <summary>
    /// Encodes <paramref name="chains"/> back to back (<c>offer_chains</c>).
    /// </summary>
    public static byte[] EncodeChains(IReadOnlyList<ChainHash> chains)
    {
        ArgumentNullException.ThrowIfNull(chains);
        var bytes = new byte[chains.Count * ChainHashLength];
        for (var i = 0; i < chains.Count; i++)
            chains[i].Value.CopyTo(bytes, i * ChainHashLength);

        return bytes;
    }

    /// <summary>
    /// Encodes a <c>blinded_payinfo*</c> list (<c>invoice_blindedpay</c>).
    /// </summary>
    /// <exception cref="ArgumentException">A payinfo's features are longer than 65535 bytes.</exception>
    public static byte[] EncodePayInfos(IReadOnlyList<BlindedPayInfo> payInfos)
    {
        ArgumentNullException.ThrowIfNull(payInfos);

        if (payInfos.Any(payInfo => payInfo.Features.Length > ushort.MaxValue))
            throw new ArgumentException("A blinded_payinfo's features must fit in a u16 length.", nameof(payInfos));

        var bytes = new byte[payInfos.Sum(BlindedPayInfoCodec.GetLength)];
        var offset = 0;
        foreach (var payInfo in payInfos)
            offset += BlindedPayInfoCodec.Write(payInfo, bytes.AsSpan(offset));

        return bytes;
    }

    /// <summary>
    /// Encodes a <c>fallback_address*</c> list (<c>invoice_fallbacks</c>).
    /// </summary>
    /// <exception cref="ArgumentException">An address is longer than 65535 bytes.</exception>
    public static byte[] EncodeFallbacks(IReadOnlyList<FallbackAddress> fallbacks)
    {
        ArgumentNullException.ThrowIfNull(fallbacks);

        var bytes = new byte[fallbacks.Sum(f => 3 + f.Address.Length)];
        var offset = 0;
        foreach (var fallback in fallbacks)
        {
            if (fallback.Address.Length > ushort.MaxValue)
                throw new ArgumentException("A fallback address must fit in a u16 length.", nameof(fallbacks));

            bytes[offset] = fallback.Version;
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset + 1), (ushort)fallback.Address.Length);
            fallback.Address.Span.CopyTo(bytes.AsSpan(offset + 3));
            offset += 3 + fallback.Address.Length;
        }

        return bytes;
    }

    /// <summary>
    /// Encodes an <c>invreq_bip_353_name</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The name or domain is longer than 255 bytes.</exception>
    public static byte[] EncodeBip353Name(Bip353Name name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Name.Length > byte.MaxValue || name.Domain.Length > byte.MaxValue)
            throw new ArgumentException("The name and domain must each fit in a u8 length.", nameof(name));

        var bytes = new byte[2 + name.Name.Length + name.Domain.Length];
        bytes[0] = (byte)name.Name.Length;
        name.Name.Span.CopyTo(bytes.AsSpan(1));
        bytes[1 + name.Name.Length] = (byte)name.Domain.Length;
        name.Domain.Span.CopyTo(bytes.AsSpan(2 + name.Name.Length));
        return bytes;
    }

    /// <summary>
    /// Whether a BOLT 12 feature bitmap (big-endian, as on the wire) sets an even bit that is not in
    /// <paramref name="knownEvenBits"/> (BOLT 12 readers: unknown odd bits are ignored, unknown even ones refuse the
    /// message).
    /// </summary>
    /// <returns>The lowest such bit, or null.</returns>
    public static int? FindUnknownEvenBit(ReadOnlySpan<byte> features, IReadOnlySet<int>? knownEvenBits = null)
    {
        for (var bit = 0; bit < features.Length * 8; bit += 2)
        {
            var b = features[features.Length - 1 - bit / 8];
            if ((b & (1 << (bit % 8))) != 0 && (knownEvenBits is null || !knownEvenBits.Contains(bit)))
                return bit;
        }

        return null;
    }

    /// <summary>
    /// Whether bit <paramref name="bit"/> is set in a big-endian feature bitmap.
    /// </summary>
    public static bool IsBitSet(ReadOnlySpan<byte> features, int bit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bit);
        if (bit / 8 >= features.Length)
            return false;

        return (features[features.Length - 1 - bit / 8] & (1 << (bit % 8))) != 0;
    }

    internal static ulong ReadTu64(Bolt12TlvRecord record) =>
        TruncatedInt.TryDecodeTu64(record.Value.Span, out var value)
            ? value
            : throw Malformed(record, "is not a minimal tu64");

    internal static uint ReadTu32(Bolt12TlvRecord record) =>
        TruncatedInt.TryDecodeTu32(record.Value.Span, out var value)
            ? value
            : throw Malformed(record, "is not a minimal tu32");

    internal static string ReadUtf8(Bolt12TlvRecord record)
    {
        try
        {
            return s_strictUtf8.GetString(record.Value.Span);
        }
        catch (DecoderFallbackException)
        {
            throw Malformed(record, "is not valid UTF-8");
        }
    }

    internal static CompactPubKey ReadPoint(Bolt12TlvRecord record)
    {
        if (record.Value.Length != PointLength)
            throw Malformed(record, $"is {record.Value.Length} bytes, not a 33-byte point");
        if (!CompressedPointValidator.IsValid(record.Value.Span))
            throw Malformed(record, "is not a point on the curve");

        return record.Value.Span;
    }

    internal static byte[] ReadFixed(Bolt12TlvRecord record, int length)
    {
        if (record.Value.Length != length)
            throw Malformed(record, $"is {record.Value.Length} bytes, not {length}");

        return record.Value.ToArray();
    }

    internal static IReadOnlyList<ChainHash> ReadChains(Bolt12TlvRecord record)
    {
        if (record.Value.Length % ChainHashLength != 0)
            throw Malformed(record, $"is {record.Value.Length} bytes, not a whole number of chain hashes");

        var chains = new List<ChainHash>(record.Value.Length / ChainHashLength);
        for (var offset = 0; offset < record.Value.Length; offset += ChainHashLength)
            chains.Add(new ChainHash(record.Value.Span.Slice(offset, ChainHashLength)));

        return chains;
    }

    /// <summary>
    /// Reads a <c>blinded_path*</c> list; every point must be on the curve.
    /// </summary>
    /// <param name="record">The record.</param>
    /// <param name="zeroHopsRequirementId">The reader requirement a path with <c>num_hops</c> 0 breaks.</param>
    internal static IReadOnlyList<WireBlindedPath> ReadPaths(Bolt12TlvRecord record, string zeroHopsRequirementId)
    {
        var data = record.Value.Span;
        var paths = new List<WireBlindedPath>();
        var offset = 0;
        while (offset < data.Length)
        {
            var rest = data[offset..];
            if (!BlindedPathCodec.TryRead(rest, out var path, out var bytesRead, out var reason))
            {
                if (HasZeroHops(rest))
                    throw new Bolt12FormatException(new Bolt12Violation(
                                                        zeroHopsRequirementId,
                                                        $"TLV {record.Type} path {paths.Count} has num_hops 0.",
                                                        record.Type));

                throw Malformed(record, $"path {paths.Count}: {reason}");
            }

            if (path.FirstNode.NodeId is { } firstNodeId && !CompressedPointValidator.IsValid(firstNodeId))
                throw Malformed(record, $"path {paths.Count} first_node_id is not a point on the curve");
            if (!CompressedPointValidator.IsValid(path.FirstPathKey))
                throw Malformed(record, $"path {paths.Count} first_path_key is not a point on the curve");
            for (var i = 0; i < path.Hops.Count; i++)
                if (!CompressedPointValidator.IsValid(path.Hops[i].BlindedNodeId))
                    throw Malformed(record, $"path {paths.Count} hop {i} blinded_node_id is not a point on the curve");

            paths.Add(path);
            offset += bytesRead;
        }

        return paths;
    }

    internal static IReadOnlyList<BlindedPayInfo> ReadPayInfos(Bolt12TlvRecord record)
    {
        var data = record.Value.Span;
        var payInfos = new List<BlindedPayInfo>();
        var offset = 0;
        while (offset < data.Length)
        {
            if (!BlindedPayInfoCodec.TryRead(data[offset..], out var payInfo, out var bytesRead, out var reason))
                throw Malformed(record, $"payinfo {payInfos.Count} {reason}");

            payInfos.Add(payInfo);
            offset += bytesRead;
        }

        return payInfos;
    }

    internal static IReadOnlyList<FallbackAddress> ReadFallbacks(Bolt12TlvRecord record)
    {
        var data = record.Value.Span;
        var fallbacks = new List<FallbackAddress>();
        var offset = 0;
        while (offset < data.Length)
        {
            var rest = data[offset..];
            if (rest.Length < 3)
                throw Malformed(record, $"fallback {fallbacks.Count} is truncated");

            var length = BinaryPrimitives.ReadUInt16BigEndian(rest[1..]);
            if (rest.Length - 3 < length)
                throw Malformed(record, $"fallback {fallbacks.Count} address runs past the end");

            fallbacks.Add(new FallbackAddress(rest[0], rest.Slice(3, length).ToArray()));
            offset += 3 + length;
        }

        return fallbacks;
    }

    internal static Bip353Name ReadBip353Name(Bolt12TlvRecord record)
    {
        var data = record.Value.Span;
        if (data.Length < 1 || data.Length < 1 + data[0] + 1)
            throw Malformed(record, "is truncated");

        var nameLength = data[0];
        var domainLength = data[1 + nameLength];
        if (data.Length != 2 + nameLength + domainLength)
            throw Malformed(record, "does not fit its name and domain lengths");

        return new Bip353Name(data.Slice(1, nameLength).ToArray(), data.Slice(2 + nameLength, domainLength).ToArray());
    }

    internal static Bolt12FormatException Malformed(Bolt12TlvRecord record, string what) =>
        new(new Bolt12Violation(Bolt12RequirementIds.TlvStream, $"TLV {record.Type} {what}.", record.Type));

    private static bool HasZeroHops(ReadOnlySpan<byte> path)
    {
        if (!SciddirOrPubkeyCodec.TryRead(path, out _, out var offset, out _))
            return false;

        offset += PointLength;
        return path.Length > offset && path[offset] == 0;
    }
}

/// <summary>
/// Carries a <see cref="Bolt12Violation"/> out of the typed readers; caught by their <c>TryParse</c>.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class Bolt12FormatException(Bolt12Violation violation) : Exception(violation.ToString())
{
    public Bolt12Violation Violation { get; } = violation;
}