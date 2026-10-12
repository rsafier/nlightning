using System.Buffers.Binary;
using NBitcoin;

namespace NLightning.Bolt11.Models.TaggedFields;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Models;
using Domain.Utils;
using Enums;
using Interfaces;

/// <summary>
/// Tagged field <c>b</c> (20): one blinded payment path, bLIP 39 ("BOLT 11 Invoice Blinded Path Tagged Field",
/// status Draft; LND 0.18+ writes and reads it, no BOLT defines it). May repeat.
/// </summary>
/// <remarks>
/// <para>The field data is one <c>blinded_payinfo</c>, in bytes (8-to-5 bit conversion, trailing bits under a byte
/// dropped):</para>
/// <code>
/// u32 fee_base_msat, u32 fee_proportional_millionths, u16 cltv_expiry_delta,
/// u64 htlc_minimum_msat, u64 htlc_maximum_msat, u16 flen, flen*byte features,
/// 33*byte first_ephemeral_blinding_point, byte num_hops,
/// num_hops * (33*byte blinded_node_pubkey, bigsize cipher_text_length, cipher_text)
/// </code>
/// <para>bLIP 39: the <c>blinded_node_pubkey</c> of the first hop is the introduction node's <b>real</b> node id (the
/// first hop's blinded id is not transmitted; a payer reaches the introduction node with an unblinded onion and the
/// <c>first_ephemeral_blinding_point</c> as <c>current_path_key</c>, BOLT 4). The decoded
/// <see cref="BlindedPaymentPath"/> therefore has <see cref="BlindedPath.FirstNodeId"/> and the first hop's
/// <see cref="BlindedPathHop.BlindedNodeId"/> both set to that real id.</para>
/// <para>Strict reader (our choice, as for the other known fields): every point must be a valid compressed secp256k1
/// point, <c>num_hops</c> at least 1, the BigSize lengths canonical, and no whole byte may follow the last hop. A
/// malformed <c>b</c> fails the invoice.</para>
/// </remarks>
internal sealed class BlindedPaymentPathTaggedField : ITaggedField
{
    /// <summary>The fixed part before the features: 4 + 4 + 2 + 8 + 8 bytes, then the u16 <c>flen</c>.</summary>
    private const int RelayInfoLength = 26;

    private const int PointLength = 33;

    /// <summary>The most bytes a tagged field can carry (1023 groups of 5 bits).</summary>
    private const int MaxDataBytes = 1023 * 5 / 8;

    private readonly byte[] _data;

    public TaggedFieldTypes Type => TaggedFieldTypes.BlindedPaymentPath;

    internal BlindedPaymentPath Value { get; }

    public short Length { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="BlindedPaymentPathTaggedField"/> class.
    /// </summary>
    /// <param name="value">The path; its first hop's id is written as the introduction node's real id.</param>
    /// <exception cref="ArgumentException">If the path has no hop or more than 255, or does not fit in a tagged
    /// field (639 bytes).</exception>
    internal BlindedPaymentPathTaggedField(BlindedPaymentPath value)
    {
        ArgumentNullException.ThrowIfNull(value);

        _data = Serialize(value);
        if (_data.Length > MaxDataBytes)
            throw new ArgumentException($"The blinded path takes {_data.Length} bytes; a tagged field holds at most "
                                      + $"{MaxDataBytes}.", nameof(value));

        Value = value;
        Length = (short)((_data.Length * 8 + 4) / 5);
    }

    /// <inheritdoc/>
    public void WriteToBitWriter(BitWriter bitWriter)
    {
        bitWriter.WriteBits(_data, _data.Length * 8);

        // Pad to the 5-bit boundary with zeros
        for (var i = _data.Length * 8; i < Length * 5; i++)
            bitWriter.WriteBit(false);
    }

    /// <inheritdoc/>
    public bool IsValid() => Value.Path.Hops.Count > 0;

    /// <summary>
    /// Reads a <see cref="BlindedPaymentPathTaggedField"/> from a <see cref="BitReader"/>.
    /// </summary>
    /// <param name="bitReader">The field's bits.</param>
    /// <param name="length">The field's <c>data_length</c> in 5-bit groups.</param>
    /// <exception cref="ArgumentException">If the data is not exactly one well-formed <c>blinded_payinfo</c>.
    /// </exception>
    internal static BlindedPaymentPathTaggedField FromBitReader(BitReader bitReader, short length)
    {
        var byteCount = length * 5 / 8;
        var data = new byte[byteCount];
        if (byteCount > 0)
            bitReader.ReadBits(data, byteCount * 8);

        return new BlindedPaymentPathTaggedField(Parse(data));
    }

    /// <summary>
    /// Parses one bLIP 39 <c>blinded_payinfo</c>.
    /// </summary>
    /// <exception cref="ArgumentException">If <paramref name="data"/> is not exactly one well-formed entry.</exception>
    internal static BlindedPaymentPath Parse(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        var relay = Take(data, ref offset, RelayInfoLength, "the relay info");
        var feeBaseMsat = BinaryPrimitives.ReadUInt32BigEndian(relay);
        var feeProportionalMillionths = BinaryPrimitives.ReadUInt32BigEndian(relay[4..]);
        var cltvExpiryDelta = BinaryPrimitives.ReadUInt16BigEndian(relay[8..]);
        var htlcMinimumMsat = BinaryPrimitives.ReadUInt64BigEndian(relay[10..]);
        var htlcMaximumMsat = BinaryPrimitives.ReadUInt64BigEndian(relay[18..]);

        var featuresLength = BinaryPrimitives.ReadUInt16BigEndian(Take(data, ref offset, 2, "flen"));
        var features = Take(data, ref offset, featuresLength, "the features").ToArray();
        var firstPathKey = ReadPoint(data, ref offset, "first_ephemeral_blinding_point");
        var hopCount = Take(data, ref offset, 1, "num_hops")[0];
        if (hopCount == 0)
            throw new ArgumentException("A blinded path must have at least one hop (num_hops is 0).");

        var hops = new List<BlindedPathHop>(hopCount);
        for (var i = 0; i < hopCount; i++)
        {
            var nodeId = ReadPoint(data, ref offset, $"blinded_node_pubkey of hop {i}");
            var cipherLength = ReadBigSize(data, ref offset, i);
            if (cipherLength > (ulong)(data.Length - offset))
                throw new ArgumentException($"The cipher_text of hop {i} ({cipherLength} bytes) is longer than the "
                                          + "remaining data.");

            var cipherText = Take(data, ref offset, (int)cipherLength, $"the cipher_text of hop {i}").ToArray();
            hops.Add(new BlindedPathHop(nodeId, cipherText));
        }

        if (offset != data.Length)
            throw new ArgumentException($"{data.Length - offset} trailing byte(s) after the last blinded hop.");

        // bLIP 39: the first blinded_node_pubkey is the introduction node's real id
        var path = new BlindedPath(hops[0].BlindedNodeId, firstPathKey, hops);
        var payInfo = new BlindedPayInfo(feeBaseMsat, feeProportionalMillionths, cltvExpiryDelta, htlcMinimumMsat,
                                         htlcMaximumMsat, features);
        return new BlindedPaymentPath(path, payInfo);
    }

    /// <summary>
    /// Writes one bLIP 39 <c>blinded_payinfo</c> for <paramref name="value"/>.
    /// </summary>
    internal static byte[] Serialize(BlindedPaymentPath value)
    {
        var path = value.Path;
        var payInfo = value.PayInfo;
        if (path.Hops.Count is 0 or > byte.MaxValue)
            throw new ArgumentException($"A blinded path needs 1 to 255 hops, not {path.Hops.Count}.",
                                        nameof(value));
        if (payInfo.Features.Length > ushort.MaxValue)
            throw new ArgumentException("The path features are too long.", nameof(value));

        using var stream = new MemoryStream();
        Span<byte> relay = stackalloc byte[RelayInfoLength + 2];
        BinaryPrimitives.WriteUInt32BigEndian(relay, payInfo.FeeBaseMsat);
        BinaryPrimitives.WriteUInt32BigEndian(relay[4..], payInfo.FeeProportionalMillionths);
        BinaryPrimitives.WriteUInt16BigEndian(relay[8..], payInfo.CltvExpiryDelta);
        BinaryPrimitives.WriteUInt64BigEndian(relay[10..], payInfo.HtlcMinimumMsat);
        BinaryPrimitives.WriteUInt64BigEndian(relay[18..], payInfo.HtlcMaximumMsat);
        BinaryPrimitives.WriteUInt16BigEndian(relay[26..], (ushort)payInfo.Features.Length);
        stream.Write(relay);
        stream.Write(payInfo.Features.Span);
        stream.Write(((byte[])path.FirstPathKey).AsSpan());
        stream.WriteByte((byte)path.Hops.Count);
        for (var i = 0; i < path.Hops.Count; i++)
        {
            // bLIP 39: the first hop carries the introduction node's real id
            var nodeId = i == 0 ? path.FirstNodeId : path.Hops[i].BlindedNodeId;
            stream.Write(((byte[])nodeId).AsSpan());
            WriteBigSize(stream, (ulong)path.Hops[i].EncryptedRecipientData.Length);
            stream.Write(path.Hops[i].EncryptedRecipientData.Span);
        }

        return stream.ToArray();
    }

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> data, ref int offset, int count, string what)
    {
        if (count > data.Length - offset)
            throw new ArgumentException($"The blinded path is truncated in {what}.");

        var slice = data.Slice(offset, count);
        offset += count;
        return slice;
    }

    private static CompactPubKey ReadPoint(ReadOnlySpan<byte> data, ref int offset, string what)
    {
        var bytes = Take(data, ref offset, PointLength, what).ToArray();
        if (!PubKey.TryCreatePubKey(bytes, out var pubKey) || !pubKey.IsCompressed)
            throw new ArgumentException($"The {what} is not a valid compressed public key.");

        return new CompactPubKey(bytes);
    }

    private static ulong ReadBigSize(ReadOnlySpan<byte> data, ref int offset, int hopIndex)
    {
        var what = $"the cipher_text_length of hop {hopIndex}";
        var first = Take(data, ref offset, 1, what)[0];
        ulong value;
        ulong minimum;
        switch (first)
        {
            case < 0xFD:
                return first;
            case 0xFD:
                value = BinaryPrimitives.ReadUInt16BigEndian(Take(data, ref offset, 2, what));
                minimum = 0xFD;
                break;
            case 0xFE:
                value = BinaryPrimitives.ReadUInt32BigEndian(Take(data, ref offset, 4, what));
                minimum = 0x10000;
                break;
            default:
                value = BinaryPrimitives.ReadUInt64BigEndian(Take(data, ref offset, 8, what));
                minimum = 0x100000000;
                break;
        }

        if (value < minimum)
            throw new ArgumentException($"The {what} is not a canonical BigSize.");

        return value;
    }

    private static void WriteBigSize(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[9];
        switch (value)
        {
            case < 0xFD:
                stream.WriteByte((byte)value);
                return;
            case <= ushort.MaxValue:
                buffer[0] = 0xFD;
                BinaryPrimitives.WriteUInt16BigEndian(buffer[1..], (ushort)value);
                stream.Write(buffer[..3]);
                return;
            case <= uint.MaxValue:
                buffer[0] = 0xFE;
                BinaryPrimitives.WriteUInt32BigEndian(buffer[1..], (uint)value);
                stream.Write(buffer[..5]);
                return;
            default:
                buffer[0] = 0xFF;
                BinaryPrimitives.WriteUInt64BigEndian(buffer[1..], value);
                stream.Write(buffer);
                return;
        }
    }
}