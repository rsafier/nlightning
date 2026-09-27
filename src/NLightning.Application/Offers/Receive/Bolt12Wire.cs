namespace NLightning.Application.Offers.Receive;

using Domain.Crypto.ValueObjects;
using Domain.Offers;
using Domain.Offers.Encoding;
using Domain.Offers.Signing;
using Domain.Protocol.Onion.Models;

/// <summary>
/// The BOLT 12 wire pieces the receive side needs: TLV stream encode and strict parse, the Merkle root of
/// "Signature Calculation", the string form (bech32 5-bit words without a checksum) and <c>blinded_payinfo</c>.
/// </summary>
/// <remarks>
/// <para>A thin seam over lane B12-A's codecs (<see cref="Bolt12TlvStream"/>, <see cref="Bolt12MerkleTree"/>,
/// <see cref="Bolt12Bech32"/>, <see cref="Bolt12FieldCodec"/>, <see cref="Bolt12TlvRanges"/>): lane B12-D carried its
/// own copy while those were contract stubs; since the B12 integration every member delegates, so the node has one
/// implementation of each. Pure and thread-safe.</para>
/// </remarks>
internal static class Bolt12Wire
{
    /// <summary>
    /// Whether <paramref name="type"/> is a signature element (BOLT 12: types 240 to 1000 inclusive).
    /// </summary>
    public static bool IsSignatureType(ulong type) => Bolt12TlvRanges.IsSignatureField(type);

    /// <summary>
    /// The wire bytes of <paramref name="records"/>: each as bigsize type, bigsize length and value, in the given
    /// order (not checked: the callers write sorted records).
    /// </summary>
    public static byte[] Encode(IEnumerable<Bolt12TlvRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        using var stream = new MemoryStream();
        foreach (var record in records)
            stream.Write(Bolt12TlvStream.EncodeRecord(record));

        return stream.ToArray();
    }

    /// <summary>
    /// One record's wire bytes.
    /// </summary>
    public static byte[] EncodeRecord(Bolt12TlvRecord record) => Bolt12TlvStream.EncodeRecord(record);

    /// <summary>
    /// Reads a TLV stream (BOLT 1, <see cref="Bolt12TlvStream.TryParse(ReadOnlyMemory{byte}, out Bolt12TlvStream?)"/>):
    /// minimal bigsize types and lengths, strictly increasing types, every length within the bytes. Types are not
    /// range-checked here.
    /// </summary>
    /// <returns>False when the bytes are not a valid TLV stream.</returns>
    public static bool TryParse(ReadOnlyMemory<byte> bytes, out Bolt12TlvStream? stream) =>
        Bolt12TlvStream.TryParse(bytes, out stream);

    /// <summary>
    /// BOLT 12 "Signature Calculation": the Merkle root of the records outside the signature range
    /// (<see cref="Bolt12MerkleTree.ComputeRoot"/>).
    /// </summary>
    /// <param name="records">The records, in strictly increasing type order.</param>
    /// <exception cref="ArgumentException">No record outside the signature range, or the types are not strictly
    /// increasing.</exception>
    public static Hash ComputeMerkleRoot(IReadOnlyList<Bolt12TlvRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        return Bolt12MerkleTree.ComputeRoot(new Bolt12TlvStream(records));
    }

    /// <summary>
    /// The BOLT 12 string form: <paramref name="hrp"/>, <c>1</c>, then the bytes as bech32 5-bit words (zero-padded),
    /// lower case and without a checksum (BOLT 12 "Encoding").
    /// </summary>
    public static string ToBolt12String(string hrp, ReadOnlySpan<byte> data) => Bolt12Bech32.Encode(hrp, data);

    /// <summary>
    /// BOLT 12 <c>blinded_payinfo</c>: <c>u32 fee_base_msat || u32 fee_proportional_millionths ||
    /// u16 cltv_expiry_delta || u64 htlc_minimum_msat || u64 htlc_maximum_msat || u16 flen || features</c>.
    /// </summary>
    public static byte[] EncodePayInfo(BlindedPayInfo payInfo)
    {
        ArgumentNullException.ThrowIfNull(payInfo);
        return Bolt12FieldCodec.EncodePayInfos([payInfo]);
    }

    /// <summary>
    /// Whether a BOLT 9 style feature bitmap (big-endian, bit 0 the least significant bit of the last byte) sets an even
    /// bit outside <paramref name="knownEvenBits"/> (BOLT 12 readers: unknown even bits are refused, odd ones ignored).
    /// </summary>
    public static bool HasUnknownEvenBit(ReadOnlySpan<byte> features, IReadOnlySet<int>? knownEvenBits = null) =>
        Bolt12FieldCodec.FindUnknownEvenBit(features, knownEvenBits) is not null;
}