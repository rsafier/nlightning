using System.Buffers.Binary;

namespace NLightning.Domain.Payments.Keysend;

using Crypto.Constants;
using Protocol.Onion.Constants;

/// <summary>
/// The custom records of an <c>update_add_htlc</c> extension (LND's <c>lnwire.CustomRecords</c>, NL-1182): TLV records
/// of type 65536 or more (<see cref="CustomRecordCodec.MinType"/>), stored on the HTLC record as a canonical TLV stream.
/// Unlike a final hop's onion records, any type at or above the minimum is allowed (LND's <c>CustomRecords.Validate</c>).
/// </summary>
public static class WireCustomRecordCodec
{
    /// <summary>BOLT 8's largest plaintext: a message's 2-byte type and body (65,535 bytes).</summary>
    public const int MaxLightningMessageLength = ushort.MaxValue;

    /// <summary>
    /// The fixed part of an <c>update_add_htlc</c>: the 2-byte type, channel_id, id, amount_msat, payment_hash,
    /// cltv_expiry and the 1,366-byte onion (1,452 bytes).
    /// </summary>
    public const int UpdateAddHtlcFixedLength = 2 + 32 + 8 + 8 + CryptoConstants.Sha256HashLen + 4
                                              + OnionConstants.PacketLength;

    /// <summary>The <c>blinded_path</c> TLV of an <c>update_add_htlc</c>: type 0, length 33, a point (35 bytes).</summary>
    public const int BlindedPathTlvLength = 1 + 1 + CryptoConstants.CompactPubkeyLen;

    /// <summary>
    /// The most bytes the custom records of an <c>update_add_htlc</c> may take once encoded (NL-1182): what BOLT 8 leaves
    /// after the fixed body and, when <paramref name="withBlindedPath"/>, the <c>blinded_path</c> TLV. A larger add could
    /// be committed and persisted but never sent (every send and retransmission would throw).
    /// </summary>
    public static int MaxEncodedLength(bool withBlindedPath) =>
        MaxLightningMessageLength - UpdateAddHtlcFixedLength - (withBlindedPath ? BlindedPathTlvLength : 0);

    /// <summary>
    /// Encodes <paramref name="records"/> like <see cref="Encode"/> and checks that they fit an <c>update_add_htlc</c>
    /// (<see cref="MaxEncodedLength"/>), before anything is staged for the add.
    /// </summary>
    /// <exception cref="ArgumentException">A record is invalid (<see cref="Validate"/>) or the records are too large.
    /// </exception>
    public static byte[] EncodeForUpdateAddHtlc(IEnumerable<CustomRecord>? records, bool withBlindedPath)
    {
        var encoded = Encode(records);
        EnsureFits(encoded.Length, withBlindedPath, nameof(records));
        return encoded;
    }

    /// <summary>
    /// Checks that <paramref name="records"/> (already validated) fit an <c>update_add_htlc</c> that may carry a
    /// <c>blinded_path</c> TLV (the stricter bound, for a resolution given before the outgoing add is known).
    /// </summary>
    /// <exception cref="ArgumentException">The records are too large.</exception>
    public static void EnsureFitsUpdateAddHtlc(IReadOnlyList<CustomRecord> records) =>
        EnsureFits(EncodedLength(records), true, nameof(records));

    /// <summary>The encoded size of <paramref name="records"/> as a TLV stream.</summary>
    public static long EncodedLength(IEnumerable<CustomRecord> records)
    {
        long length = 0;
        foreach (var record in records)
            length += BigSizeLength(record.Type) + BigSizeLength((ulong)record.Value.Length) + record.Value.Length;
        return length;
    }

    private static void EnsureFits(long encodedLength, bool withBlindedPath, string paramName)
    {
        var max = MaxEncodedLength(withBlindedPath);
        if (encodedLength > max)
            throw new ArgumentException(
                $"custom records take {encodedLength} bytes encoded; an update_add_htlc has room for at most {max}",
                paramName);
    }

    private static int BigSizeLength(ulong value) => value switch
    {
        < 0xfd => 1,
        <= ushort.MaxValue => 3,
        <= uint.MaxValue => 5,
        _ => 9
    };

    /// <summary>
    /// Returns the records sorted by type.
    /// </summary>
    /// <exception cref="ArgumentException">A type is below <see cref="CustomRecordCodec.MinType"/> (LND: "custom records
    /// entry with TLV type below min: 65536") or appears twice.</exception>
    public static IReadOnlyList<CustomRecord> Validate(IEnumerable<CustomRecord>? records)
    {
        if (records is null)
            return [];

        var sorted = new List<CustomRecord>();
        foreach (var record in records)
        {
            ArgumentNullException.ThrowIfNull(record, nameof(records));
            if (record.Type < CustomRecordCodec.MinType)
                throw new ArgumentException(
                    $"custom records entry with TLV type below min: {CustomRecordCodec.MinType}", nameof(records));

            sorted.Add(record);
        }

        sorted.Sort((a, b) => a.Type.CompareTo(b.Type));
        for (var i = 1; i < sorted.Count; i++)
        {
            if (sorted[i].Type == sorted[i - 1].Type)
                throw new ArgumentException($"Custom record type {sorted[i].Type} appears twice.", nameof(records));
        }

        return sorted;
    }

    /// <summary>Encodes <paramref name="records"/> (checked by <see cref="Validate"/>) as a TLV stream; empty for none.</summary>
    public static byte[] Encode(IEnumerable<CustomRecord>? records)
    {
        var sorted = Validate(records);
        if (sorted.Count == 0)
            return [];

        using var stream = new MemoryStream();
        foreach (var record in sorted)
        {
            WriteBigSize(stream, record.Type);
            WriteBigSize(stream, (ulong)record.Value.Length);
            stream.Write(record.Value.Span);
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Decodes a stream written by <see cref="Encode"/>; bytes that are not a canonical stream of custom records give no
    /// records (a stored row never breaks a channel load).
    /// </summary>
    public static IReadOnlyList<CustomRecord> Decode(ReadOnlyMemory<byte> bytes) =>
        bytes.IsEmpty ? [] : CustomRecordCodec.TryDecode(bytes.Span, out var records) ? records : [];

    private static void WriteBigSize(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[9];
        int length;
        switch (value)
        {
            case < 0xfd:
                buffer[0] = (byte)value;
                length = 1;
                break;
            case <= ushort.MaxValue:
                buffer[0] = 0xfd;
                BinaryPrimitives.WriteUInt16BigEndian(buffer[1..], (ushort)value);
                length = 3;
                break;
            case <= uint.MaxValue:
                buffer[0] = 0xfe;
                BinaryPrimitives.WriteUInt32BigEndian(buffer[1..], (uint)value);
                length = 5;
                break;
            default:
                buffer[0] = 0xff;
                BinaryPrimitives.WriteUInt64BigEndian(buffer[1..], value);
                length = 9;
                break;
        }

        stream.Write(buffer[..length]);
    }
}