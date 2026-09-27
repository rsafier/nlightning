using System.Text;

namespace NLightning.Application.Offers.Send;

using Domain.Crypto.ValueObjects;
using Domain.Offers;
using Domain.Offers.Encoding;
using Domain.Offers.Signing;

/// <summary>
/// The BOLT 12 wire pieces the payer needs: the string format (bech32 without a checksum), the TLV stream codec and
/// the Merkle root of "Signature Calculation".
/// </summary>
/// <remarks>
/// <para>A thin seam over lane B12-A's codecs (<see cref="Bolt12Bech32"/>, <see cref="Bolt12TlvStream"/>,
/// <see cref="Bolt12MerkleTree"/>, <see cref="Bolt12TlvRanges"/>): step 1 of lane B12-E carried its own copy while
/// those were contract stubs; step 2 delegates to them, so the payer has one implementation. The integrator may inline
/// the call sites and delete this class.</para>
/// <para>Pure and thread-safe.</para>
/// </remarks>
internal static class Bolt12Wire
{
    /// <summary>
    /// BOLT 12 "Encoding" reader: the data of a bolt12 string with the prefix <paramref name="expectedHrp"/>
    /// (<see cref="Bolt12Bech32.Decode"/>: all lowercase or all uppercase, <c>+</c> continuations).
    /// </summary>
    /// <exception cref="FormatException">The string is not a bolt12 string with that prefix.</exception>
    public static byte[] DecodeString(string value, string expectedHrp)
    {
        ArgumentNullException.ThrowIfNull(value);

        var (hrp, data) = Bolt12Bech32.Decode(value.Trim());
        if (hrp != expectedHrp)
            throw new FormatException($"The bolt12 string's prefix is '{hrp}', not '{expectedHrp}'.");

        return data;
    }

    /// <summary>
    /// BOLT 12 "Encoding" writer: <c>hrp || "1" || bech32(data)</c>, lowercase, no checksum.
    /// </summary>
    public static string EncodeString(string hrp, ReadOnlySpan<byte> data) => Bolt12Bech32.Encode(hrp, data);

    /// <summary>
    /// Reads a TLV stream (BOLT 1, <see cref="Bolt12TlvStream.Parse"/>): strictly increasing types, minimal BigSize
    /// types and lengths, lengths within the bytes. Types are not range-checked.
    /// </summary>
    /// <exception cref="FormatException">The bytes are not a valid TLV stream.</exception>
    public static Bolt12TlvStream ParseStream(ReadOnlyMemory<byte> bytes) => Bolt12TlvStream.Parse(bytes);

    /// <summary>
    /// The stream's wire bytes.
    /// </summary>
    public static byte[] Encode(Bolt12TlvStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return stream.Encode();
    }

    /// <summary>
    /// BOLT 12 "Signature Calculation": the Merkle root over the records outside the signature range (240-1000).
    /// </summary>
    /// <exception cref="ArgumentException">No record outside the signature range.</exception>
    public static Hash MerkleRoot(Bolt12TlvStream stream) => Bolt12MerkleTree.ComputeRoot(stream);

    /// <summary>
    /// <c>H(tag, msg) = SHA256(SHA256(tag) || SHA256(tag) || msg)</c>.
    /// </summary>
    public static byte[] TaggedHash(string tag, ReadOnlySpan<byte> message) =>
        Bolt12MerkleTree.TaggedHash(Encoding.UTF8.GetBytes(tag), message);

    /// <summary>
    /// Whether <paramref name="type"/> is a signature element (240 to 1000 inclusive).
    /// </summary>
    public static bool IsSignatureType(ulong type) => Bolt12TlvRanges.IsSignatureField(type);

    /// <summary>
    /// One record's wire bytes (type, length, value).
    /// </summary>
    public static byte[] EncodeRecord(Bolt12TlvRecord record) => Bolt12TlvStream.EncodeRecord(record);
}