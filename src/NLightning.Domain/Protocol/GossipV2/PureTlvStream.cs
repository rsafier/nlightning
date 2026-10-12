namespace NLightning.Domain.Protocol.GossipV2;

using Tlv;

/// <summary>
/// One record of a pure TLV message (taproot gossip, BOLTs PR #1059): its type and its value bytes exactly as they are
/// on the wire.
/// </summary>
public sealed record PureTlvRecord
{
    private readonly byte[] _value;

    public PureTlvRecord(ulong type, ReadOnlySpan<byte> value)
    {
        Type = type;
        _value = value.ToArray();
    }

    /// <summary>The record's type.</summary>
    public ulong Type { get; }

    /// <summary>The record's value bytes (a read-only view; the record keeps its own copy).</summary>
    public ReadOnlyMemory<byte> Value => _value;

    /// <summary>The encoded length of the record: <c>bigsize type || bigsize length || value</c>.</summary>
    public int EncodedLength =>
        BigSizeCodec.GetLength(Type) + BigSizeCodec.GetLength((ulong)_value.Length) + _value.Length;

    public bool Equals(PureTlvRecord? other) =>
        other is not null && Type == other.Type && _value.AsSpan().SequenceEqual(other._value);

    public override int GetHashCode() => HashCode.Combine(Type, _value.Length);

    internal int WriteTo(Span<byte> destination)
    {
        var offset = BigSizeCodec.Write(Type, destination);
        offset += BigSizeCodec.Write((ulong)_value.Length, destination[offset..]);
        _value.CopyTo(destination[offset..]);
        return offset + _value.Length;
    }
}

/// <summary>
/// The records of a pure TLV message (taproot gossip, BOLTs PR #1059 "Pure TLV messages"), in wire order. The
/// messages have no fixed fields: every field is a record, the signature lives at type 240 and covers the
/// <see cref="GetSignedBytes">signed range</see> (types 0-239 and 1,000,000,000-2,999,999,999) exactly as received,
/// unknown odd records included. Keeping the records as they came (not rebuilding them from typed fields) is what
/// makes a received message re-serialize byte for byte, so its signature stays valid when it is stored, served and
/// relayed.
/// </summary>
/// <remarks>
/// Parsing applies BOLT 1's stream rules: canonical BigSize, strictly increasing types, every length within the
/// message, and an unknown even type (one not in the caller's known set) rejected. This is the Domain codec of the
/// four v2 gossip payloads (as <c>ChannelAnnouncementPayload</c> is for v1); the wire definitions in
/// Infrastructure.Serialization validate the same rules with their TLV tables and hand the bytes here.
/// </remarks>
public sealed class PureTlvStream
{
    /// <summary>The first unsigned type of the first range (the signature records, 240 on).</summary>
    public const ulong UnsignedRangeOneStart = 240;

    /// <summary>The first type of the second signed range.</summary>
    public const ulong SignedRangeTwoStart = 1_000_000_000;

    /// <summary>The first unsigned type after the second signed range.</summary>
    public const ulong UnsignedRangeTwoStart = 3_000_000_000;

    private readonly PureTlvRecord[] _records;

    /// <summary>Creates a stream from records already in strictly increasing type order.</summary>
    /// <exception cref="ArgumentException">The types are not strictly increasing.</exception>
    public PureTlvStream(IEnumerable<PureTlvRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = records.ToArray();
        for (var i = 1; i < _records.Length; i++)
            if (_records[i].Type <= _records[i - 1].Type)
                throw new ArgumentException(
                    $"TLV type {_records[i].Type} is not greater than the previous type {_records[i - 1].Type}.",
                    nameof(records));
    }

    /// <summary>The records in wire (ascending type) order.</summary>
    public IReadOnlyList<PureTlvRecord> Records => _records;

    /// <summary>Whether <paramref name="type"/> is in the signed range (0-239 or 1,000,000,000-2,999,999,999).</summary>
    public static bool IsSignedType(ulong type) =>
        type < UnsignedRangeOneStart || type is >= SignedRangeTwoStart and < UnsignedRangeTwoStart;

    /// <summary>The value of the record of <paramref name="type"/>, or null when the stream has none.</summary>
    public ReadOnlyMemory<byte>? Get(ulong type)
    {
        foreach (var record in _records)
        {
            if (record.Type == type)
                return record.Value;
            if (record.Type > type)
                break;
        }

        return null;
    }

    /// <summary>Whether the stream carries a record of <paramref name="type"/>.</summary>
    public bool Contains(ulong type) => Get(type) is not null;

    /// <summary>The wire bytes of the whole stream (every record, in order).</summary>
    public byte[] GetBytes() => Serialize(_records);

    /// <summary>
    /// The bytes a v2 gossip signature covers (BOLTs PR #1059 "Pure TLV messages"): every record whose type is in the
    /// signed range, in ascending order, each as <c>bigsize type || bigsize length || value</c> exactly as on the wire.
    /// </summary>
    public byte[] GetSignedBytes() => Serialize(_records.Where(r => IsSignedType(r.Type)));

    /// <summary>
    /// Returns a stream with <paramref name="record"/> added, or replacing the record of the same type (the other
    /// records keep their bytes).
    /// </summary>
    public PureTlvStream With(PureTlvRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new PureTlvStream(_records.Where(r => r.Type != record.Type).Append(record).OrderBy(r => r.Type));
    }

    /// <summary>
    /// Parses a pure TLV stream with BOLT 1's rules.
    /// </summary>
    /// <param name="bytes">The whole message body (without the message type).</param>
    /// <param name="knownTypes">The message's known types; an unknown even type fails the stream.</param>
    /// <exception cref="FormatException">The stream breaks a BOLT 1 rule.</exception>
    public static PureTlvStream Parse(ReadOnlySpan<byte> bytes, IReadOnlySet<ulong> knownTypes)
    {
        ArgumentNullException.ThrowIfNull(knownTypes);
        var records = new List<PureTlvRecord>();
        var offset = 0;
        ulong? previous = null;
        while (offset < bytes.Length)
        {
            if (!BigSizeCodec.TryRead(bytes[offset..], out var type, out var typeLength))
                throw new FormatException("Invalid bigsize encoding of a TLV type.");
            offset += typeLength;
            if (!BigSizeCodec.TryRead(bytes[offset..], out var length, out var lengthLength))
                throw new FormatException($"Invalid bigsize encoding of the length of TLV type {type}.");
            offset += lengthLength;

            if (length > (ulong)(bytes.Length - offset))
                throw new FormatException(
                    $"TLV length {length} exceeds the {bytes.Length - offset} bytes remaining in the message.");
            if (previous is { } p && type <= p)
                throw new FormatException($"TLV type {type} is not greater than the previous type {p}.");
            if (type % 2 == 0 && !knownTypes.Contains(type))
                throw new FormatException($"Unknown even TLV type {type}.");

            records.Add(new PureTlvRecord(type, bytes.Slice(offset, (int)length)));
            offset += (int)length;
            previous = type;
        }

        return new PureTlvStream(records);
    }

    private static byte[] Serialize(IEnumerable<PureTlvRecord> records)
    {
        var list = records as ICollection<PureTlvRecord> ?? records.ToList();
        var bytes = new byte[list.Sum(r => r.EncodedLength)];
        var offset = 0;
        foreach (var record in list)
            offset += record.WriteTo(bytes.AsSpan(offset));

        return bytes;
    }
}