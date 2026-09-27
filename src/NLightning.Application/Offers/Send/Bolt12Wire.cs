using System.Security.Cryptography;
using System.Text;

namespace NLightning.Application.Offers.Send;

using Domain.Crypto.ValueObjects;
using Domain.Offers;
using Domain.Offers.Constants;
using Domain.Protocol.Tlv;

/// <summary>
/// The BOLT 12 wire pieces the payer needs: the string format (bech32 without a checksum), the TLV stream codec and
/// the Merkle root of "Signature Calculation".
/// </summary>
/// <remarks>
/// <para>Stand-in for lane B12-A (B0-T1, B0-T2, B0-T5: <c>Bolt12Bech32</c>, <c>Bolt12TlvStream.Parse/Encode</c>,
/// <c>Bolt12MerkleTree</c>), which were contract stubs when lane B12-E started. The integrator points these members at
/// lane A's types and deletes the bodies (the same seam as wave M6's codec copy).</para>
/// <para>Pure and thread-safe.</para>
/// </remarks>
internal static class Bolt12Wire
{
    private const string Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";

    /// <summary>
    /// BOLT 12 "Encoding" reader: the data of a bolt12 string with the prefix <paramref name="expectedHrp"/>. All
    /// lowercase or all uppercase; a <c>+</c> followed by whitespace between two characters is removed.
    /// </summary>
    /// <exception cref="FormatException">The string is not a bolt12 string with that prefix.</exception>
    public static byte[] DecodeString(string value, string expectedHrp)
    {
        ArgumentNullException.ThrowIfNull(value);

        var joined = RemoveContinuations(value.Trim());
        var hasLower = joined.Any(char.IsLower);
        var hasUpper = joined.Any(char.IsUpper);
        if (hasLower && hasUpper)
            throw new FormatException("A bolt12 string must be all lowercase or all uppercase.");

        var text = joined.ToLowerInvariant();
        var separator = text.LastIndexOf('1');
        if (separator <= 0)
            throw new FormatException("The bolt12 string has no prefix.");
        if (text[..separator] != expectedHrp)
            throw new FormatException($"The bolt12 string's prefix is '{text[..separator]}', not '{expectedHrp}'.");

        var data = text[(separator + 1)..];
        var bytes = new List<byte>(data.Length * 5 / 8);
        var accumulator = 0;
        var bits = 0;
        foreach (var c in data)
        {
            var digit = Charset.IndexOf(c);
            if (digit < 0)
                throw new FormatException($"'{c}' is not a bech32 character.");

            accumulator = (accumulator << 5) | digit;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)(accumulator >> bits));
                accumulator &= (1 << bits) - 1;
            }
        }

        if (bits >= 5 || accumulator != 0)
            throw new FormatException("The bolt12 string has invalid padding.");

        return [.. bytes];
    }

    /// <summary>
    /// BOLT 12 "Encoding" writer: <c>hrp || "1" || bech32(data)</c>, lowercase, no checksum.
    /// </summary>
    public static string EncodeString(string hrp, ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder(hrp.Length + 1 + (data.Length * 8 + 4) / 5);
        builder.Append(hrp).Append('1');
        var accumulator = 0;
        var bits = 0;
        foreach (var b in data)
        {
            accumulator = (accumulator << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                builder.Append(Charset[(accumulator >> bits) & 31]);
            }

            accumulator &= (1 << bits) - 1;
        }

        if (bits > 0)
            builder.Append(Charset[(accumulator << (5 - bits)) & 31]);

        return builder.ToString();
    }

    /// <summary>
    /// Reads a TLV stream (BOLT 1): strictly increasing types, minimal BigSize types and lengths, lengths within the
    /// bytes. Types are not range-checked.
    /// </summary>
    /// <exception cref="FormatException">The bytes are not a valid TLV stream.</exception>
    public static Bolt12TlvStream ParseStream(ReadOnlyMemory<byte> bytes)
    {
        var records = new List<Bolt12TlvRecord>();
        var span = bytes.Span;
        var offset = 0;
        ulong? previous = null;
        while (offset < span.Length)
        {
            if (!BigSizeCodec.TryRead(span[offset..], out var type, out var typeLength))
                throw new FormatException($"Invalid TLV type at offset {offset}.");
            offset += typeLength;
            if (previous is { } last && type <= last)
                throw new FormatException($"TLV type {type} is not above the previous type {last}.");
            if (!BigSizeCodec.TryRead(span[offset..], out var length, out var lengthLength))
                throw new FormatException($"Invalid length of TLV type {type}.");
            offset += lengthLength;
            if (length > (ulong)(span.Length - offset))
                throw new FormatException($"TLV type {type} runs past the end.");

            records.Add(new Bolt12TlvRecord(type, bytes.Slice(offset, (int)length).ToArray()));
            offset += (int)length;
            previous = type;
        }

        return new Bolt12TlvStream(records);
    }

    /// <summary>
    /// The stream's wire bytes.
    /// </summary>
    public static byte[] Encode(Bolt12TlvStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var output = new MemoryStream();
        foreach (var record in stream.Records)
            WriteRecord(output, record);
        return output.ToArray();
    }

    /// <summary>
    /// BOLT 12 "Signature Calculation": the Merkle root over the records outside the signature range (240-1000).
    /// </summary>
    /// <exception cref="ArgumentException">No record outside the signature range.</exception>
    public static Hash MerkleRoot(Bolt12TlvStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var records = stream.Records.Where(r => !IsSignatureType(r.Type)).ToList();
        if (records.Count == 0)
            throw new ArgumentException("A Merkle tree needs at least one TLV.", nameof(stream));

        var nonceTag = SHA256.HashData([.. "LnNonce"u8, .. EncodeRecord(records[0])]);
        var leafTag = SHA256.HashData("LnLeaf"u8);
        var branchTag = SHA256.HashData("LnBranch"u8);
        var nodes = new byte[records.Count][];
        for (var i = 0; i < records.Count; i++)
        {
            var leaf = TaggedHash(leafTag, EncodeRecord(records[i]));
            var typeBytes = new byte[BigSizeCodec.GetLength(records[i].Type)];
            BigSizeCodec.Write(records[i].Type, typeBytes);
            var nonce = TaggedHash(nonceTag, typeBytes);
            nodes[i] = Branch(branchTag, leaf, nonce);
        }

        // Pair neighbours level by level; an odd one out moves up unchanged, so the lowest-order leaves end deepest
        for (var step = 1; step < nodes.Length; step *= 2)
            for (var i = 0; i + step < nodes.Length; i += 2 * step)
                nodes[i] = Branch(branchTag, nodes[i], nodes[i + step]);

        return new Hash(nodes[0]);
    }

    /// <summary>
    /// <c>H(tag, msg) = SHA256(SHA256(tag) || SHA256(tag) || msg)</c>.
    /// </summary>
    public static byte[] TaggedHash(string tag, ReadOnlySpan<byte> message) =>
        TaggedHash(SHA256.HashData(Encoding.UTF8.GetBytes(tag)), message);

    /// <summary>
    /// Whether <paramref name="type"/> is a signature element (240 to 1000 inclusive).
    /// </summary>
    public static bool IsSignatureType(ulong type) =>
        type is >= Bolt12Constants.SignatureRangeStart and <= Bolt12Constants.SignatureRangeEnd;

    /// <summary>
    /// One record's wire bytes (type, length, value).
    /// </summary>
    public static byte[] EncodeRecord(Bolt12TlvRecord record)
    {
        using var output = new MemoryStream();
        WriteRecord(output, record);
        return output.ToArray();
    }

    private static void WriteRecord(Stream output, Bolt12TlvRecord record)
    {
        Span<byte> bigSize = stackalloc byte[9];
        output.Write(bigSize[..BigSizeCodec.Write(record.Type, bigSize)]);
        output.Write(bigSize[..BigSizeCodec.Write((ulong)record.Value.Length, bigSize)]);
        output.Write(record.Value.Span);
    }

    private static byte[] Branch(byte[] branchTag, byte[] a, byte[] b) =>
        a.AsSpan().SequenceCompareTo(b) < 0
            ? TaggedHash(branchTag, [.. a, .. b])
            : TaggedHash(branchTag, [.. b, .. a]);

    private static byte[] TaggedHash(byte[] tagHash, ReadOnlySpan<byte> message)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(tagHash);
        sha.AppendData(tagHash);
        sha.AppendData(message);
        return sha.GetHashAndReset();
    }

    private static string RemoveContinuations(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '+')
            {
                if (char.IsWhiteSpace(value[i]))
                    throw new FormatException("Whitespace is only allowed after a '+'.");
                builder.Append(value[i]);
                continue;
            }

            if (builder.Length == 0 || !char.IsLetterOrDigit(builder[^1]))
                throw new FormatException("A '+' must follow a bech32 character.");

            var next = i + 1;
            while (next < value.Length && char.IsWhiteSpace(value[next]))
                next++;
            if (next >= value.Length || !char.IsLetterOrDigit(value[next]))
                throw new FormatException("A '+' must be followed by a bech32 character.");

            i = next - 1;
        }

        return builder.ToString();
    }
}