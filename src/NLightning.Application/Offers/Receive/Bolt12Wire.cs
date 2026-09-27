using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace NLightning.Application.Offers.Receive;

using Domain.Crypto.ValueObjects;
using Domain.Offers;
using Domain.Offers.Constants;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Tlv;

/// <summary>
/// The BOLT 12 wire pieces the receive side needs: TLV stream encode and strict parse, the Merkle root of
/// "Signature Calculation", the string form (bech32 5-bit words without a checksum) and <c>blinded_payinfo</c>.
/// </summary>
/// <remarks>
/// <para>Lane B12-D codes against the B12-0 contracts while lane B12-A implements <see cref="Bolt12TlvStream"/>'s
/// <c>Parse</c>/<c>Encode</c>, <c>Bolt12MerkleTree</c> and <c>Bolt12Bech32</c> in parallel (their contract members
/// throw until then). This file holds the receive side's own copy so the lane builds and tests on its own; the
/// integrator swaps each member for lane B12-A's (one call site each) and deletes it. The Merkle root is proven against
/// <c>bolt12/signature-test.json</c> in <c>OfferWireTests</c>.</para>
/// </remarks>
internal static class Bolt12Wire
{
    private const string Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";

    /// <summary>The length of an encoded <c>blinded_payinfo</c> without its features.</summary>
    public const int PayInfoFixedLength = 4 + 4 + 2 + 8 + 8 + 2;

    /// <summary>
    /// Whether <paramref name="type"/> is a signature element (BOLT 12: types 240 to 1000 inclusive).
    /// </summary>
    public static bool IsSignatureType(ulong type) =>
        type is >= Bolt12Constants.SignatureRangeStart and <= Bolt12Constants.SignatureRangeEnd;

    /// <summary>
    /// The wire bytes of <paramref name="records"/>: each as bigsize type, bigsize length and value, in order.
    /// </summary>
    public static byte[] Encode(IEnumerable<Bolt12TlvRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        using var stream = new MemoryStream();
        foreach (var record in records)
            stream.Write(EncodeRecord(record));

        return stream.ToArray();
    }

    /// <summary>
    /// One record's wire bytes.
    /// </summary>
    public static byte[] EncodeRecord(Bolt12TlvRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var length = (ulong)record.Value.Length;
        var bytes = new byte[BigSizeCodec.GetLength(record.Type) + BigSizeCodec.GetLength(length) + record.Value.Length];
        var offset = BigSizeCodec.Write(record.Type, bytes);
        offset += BigSizeCodec.Write(length, bytes.AsSpan(offset));
        record.Value.Span.CopyTo(bytes.AsSpan(offset));
        return bytes;
    }

    /// <summary>
    /// Reads a TLV stream (BOLT 1): minimal bigsize types and lengths, strictly increasing types, every length within
    /// the bytes. Types are not range-checked here.
    /// </summary>
    /// <returns>False when the bytes are not a valid TLV stream.</returns>
    public static bool TryParse(ReadOnlyMemory<byte> bytes, out Bolt12TlvStream? stream)
    {
        stream = null;
        var records = new List<Bolt12TlvRecord>();
        var span = bytes.Span;
        var offset = 0;
        ulong? previous = null;
        while (offset < span.Length)
        {
            if (!BigSizeCodec.TryRead(span[offset..], out var type, out var typeLength))
                return false;

            offset += typeLength;
            if (!BigSizeCodec.TryRead(span[offset..], out var length, out var lengthLength))
                return false;

            offset += lengthLength;
            if (previous is { } last && type <= last)
                return false;

            if (length > (ulong)(span.Length - offset))
                return false;

            records.Add(new Bolt12TlvRecord(type, bytes.Slice(offset, (int)length)));
            offset += (int)length;
            previous = type;
        }

        stream = new Bolt12TlvStream(records);
        return true;
    }

    /// <summary>
    /// BOLT 12 "Signature Calculation": the Merkle root of the records outside the signature range. Each record's leaf
    /// <c>H("LnLeaf", tlv)</c> is paired with its nonce leaf <c>H("LnNonce" || first_tlv, type)</c> in an
    /// <c>H("LnBranch", lesser || greater)</c>; those are then paired left to right, level by level, an odd one moving
    /// up unchanged, so the tree is deepest on the lowest-order leaves.
    /// </summary>
    /// <exception cref="ArgumentException">No record outside the signature range.</exception>
    public static Hash ComputeMerkleRoot(IReadOnlyList<Bolt12TlvRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var signed = records.Where(r => !IsSignatureType(r.Type)).ToList();
        if (signed.Count == 0)
            throw new ArgumentException("A Merkle tree needs at least one TLV outside the signature range.",
                                        nameof(records));

        // "first-tlv is the numerically-first TLV entry in the stream"
        var firstTlv = EncodeRecord(records.MinBy(r => r.Type)!);
        var nonceTag = new byte[Encoding.ASCII.GetByteCount(Bolt12Constants.MerkleNonceTag) + firstTlv.Length];
        Encoding.ASCII.GetBytes(Bolt12Constants.MerkleNonceTag, nonceTag);
        firstTlv.CopyTo(nonceTag, nonceTag.Length - firstTlv.Length);
        var leafTagHash = SHA256.HashData(Encoding.ASCII.GetBytes(Bolt12Constants.MerkleLeafTag));
        var nonceTagHash = SHA256.HashData(nonceTag);
        var branchTagHash = SHA256.HashData(Encoding.ASCII.GetBytes(Bolt12Constants.MerkleBranchTag));

        var level = new List<byte[]>(signed.Count);
        foreach (var record in signed)
        {
            var leaf = TaggedHash(leafTagHash, EncodeRecord(record));
            var typeBytes = new byte[BigSizeCodec.GetLength(record.Type)];
            BigSizeCodec.Write(record.Type, typeBytes);
            var nonce = TaggedHash(nonceTagHash, typeBytes);
            level.Add(Branch(branchTagHash, leaf, nonce));
        }

        while (level.Count > 1)
        {
            var next = new List<byte[]>((level.Count + 1) / 2);
            for (var i = 0; i < level.Count; i += 2)
                next.Add(i + 1 < level.Count ? Branch(branchTagHash, level[i], level[i + 1]) : level[i]);
            level = next;
        }

        return new Hash(level[0]);
    }

    /// <summary>
    /// The BOLT 12 string form: <paramref name="hrp"/>, <c>1</c>, then the bytes as bech32 5-bit words (zero-padded),
    /// lower case and without a checksum (BOLT 12 "Encoding").
    /// </summary>
    public static string ToBolt12String(string hrp, ReadOnlySpan<byte> data)
    {
        ArgumentException.ThrowIfNullOrEmpty(hrp);
        var builder = new StringBuilder(hrp.Length + 1 + (data.Length * 8 + 4) / 5);
        builder.Append(hrp).Append('1');
        int accumulator = 0, bits = 0;
        foreach (var b in data)
        {
            accumulator = ((accumulator << 8) | b) & 0xFFF;
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
    /// BOLT 12 <c>blinded_payinfo</c>: <c>u32 fee_base_msat || u32 fee_proportional_millionths ||
    /// u16 cltv_expiry_delta || u64 htlc_minimum_msat || u64 htlc_maximum_msat || u16 flen || features</c>.
    /// </summary>
    public static byte[] EncodePayInfo(BlindedPayInfo payInfo)
    {
        ArgumentNullException.ThrowIfNull(payInfo);
        var bytes = new byte[PayInfoFixedLength + payInfo.Features.Length];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span, payInfo.FeeBaseMsat);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], payInfo.FeeProportionalMillionths);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], payInfo.CltvExpiryDelta);
        BinaryPrimitives.WriteUInt64BigEndian(span[10..], payInfo.HtlcMinimumMsat);
        BinaryPrimitives.WriteUInt64BigEndian(span[18..], payInfo.HtlcMaximumMsat);
        BinaryPrimitives.WriteUInt16BigEndian(span[26..], checked((ushort)payInfo.Features.Length));
        payInfo.Features.Span.CopyTo(span[PayInfoFixedLength..]);
        return bytes;
    }

    /// <summary>
    /// Whether a BOLT 9 style feature bitmap (big-endian, bit 0 the least significant bit of the last byte) sets an even
    /// bit that <paramref name="isKnown"/> does not accept (BOLT 12 readers: unknown even bits are refused, odd ones
    /// ignored).
    /// </summary>
    public static bool HasUnknownEvenBit(ReadOnlySpan<byte> features, Func<int, bool> isKnown)
    {
        ArgumentNullException.ThrowIfNull(isKnown);
        for (var i = 0; i < features.Length; i++)
        {
            var value = features[features.Length - 1 - i];
            for (var bit = 0; bit < 8; bit += 2)
            {
                if ((value & (1 << bit)) != 0 && !isKnown(i * 8 + bit))
                    return true;
            }
        }

        return false;
    }

    private static byte[] Branch(byte[] branchTagHash, byte[] a, byte[] b)
    {
        var message = new byte[64];
        var (lesser, greater) = a.AsSpan().SequenceCompareTo(b) <= 0 ? (a, b) : (b, a);
        lesser.CopyTo(message, 0);
        greater.CopyTo(message, 32);
        return TaggedHash(branchTagHash, message);
    }

    private static byte[] TaggedHash(byte[] tagHash, ReadOnlySpan<byte> message)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(tagHash);
        sha.AppendData(tagHash);
        sha.AppendData(message);
        return sha.GetHashAndReset();
    }
}