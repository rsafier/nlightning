using System.Buffers.Binary;
using System.Runtime.Serialization;

namespace NLightning.Infrastructure.Serialization.Wire;

using Domain.Protocol.Tlv;

/// <summary>
/// A forward-only big-endian reader over one message's body: the span-based primitive layer of the wire codec
/// (plan <c>docs/agents/CODEC_REDESIGN_PLAN.md</code>). Every read is exact: a truncated message throws
/// <see cref="SerializationException"/>, which the message wire wraps like the hand-written serializers did.
/// </summary>
internal ref struct WireReader(ReadOnlySpan<byte> span)
{
    public readonly ReadOnlySpan<byte> Span = span;
    public int Position;

    public readonly int Remaining => Span.Length - Position;

    public byte U8()
    {
        if (Remaining < 1)
            throw Truncated(1);
        return Span[Position++];
    }

    public ushort U16()
    {
        if (Remaining < 2)
            throw Truncated(2);
        var value = BinaryPrimitives.ReadUInt16BigEndian(Span[Position..]);
        Position += 2;
        return value;
    }

    public uint U32()
    {
        if (Remaining < 4)
            throw Truncated(4);
        var value = BinaryPrimitives.ReadUInt32BigEndian(Span[Position..]);
        Position += 4;
        return value;
    }

    public ulong U64()
    {
        if (Remaining < 8)
            throw Truncated(8);
        var value = BinaryPrimitives.ReadUInt64BigEndian(Span[Position..]);
        Position += 8;
        return value;
    }

    /// <summary>
    /// Reads a BOLT 1 <c>bigsize</c>. Non-minimal encodings are rejected, like the stream-based serializer.
    /// </summary>
    public ulong BigSize()
    {
        if (!BigSizeCodec.TryRead(Span[Position..], out var value, out var length))
            throw new SerializationException("Invalid bigsize encoding.");
        Position += length;
        return value;
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes.</summary>
    public ReadOnlySpan<byte> Bytes(int count)
    {
        if (Remaining < count)
            throw Truncated(count);
        var value = Span.Slice(Position, count);
        Position += count;
        return value;
    }

    public byte[] BytesArray(int count) => Bytes(count).ToArray();

    /// <summary>Reads all the bytes left in the message.</summary>
    public readonly ReadOnlySpan<byte> RemainingBytes() => Span[Position..];

    private readonly SerializationException Truncated(int needed) =>
        new($"Unexpected end of message: {needed} bytes needed, {Remaining} remain.");
}