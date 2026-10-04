using System.Buffers;
using System.Buffers.Binary;

namespace NLightning.Infrastructure.Serialization.Wire;
/// <summary>
/// A forward-only big-endian writer over an <see cref="ArrayPool{T}"/>-backed, growable buffer: the span-based
/// primitive layer of the wire codec. <see cref="WrittenSpan"/> is the encoded message body (plus extension);
/// the caller returns the underlying buffer with <see cref="ReturnBuffer"/> after taking the bytes.
/// </summary>
internal ref struct WireWriter
{
    private byte[] _buffer;
    private int _position;

    public WireWriter() : this(256)
    {
    }

    private WireWriter(int initialCapacity)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
        _position = 0;
    }

    public readonly int Length => _position;

    public readonly ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _position);

    public void U8(byte value)
    {
        Ensure(1);
        _buffer[_position++] = value;
    }

    public void U16(ushort value)
    {
        Ensure(2);
        BinaryPrimitives.WriteUInt16BigEndian(_buffer.AsSpan(_position), value);
        _position += 2;
    }

    public void U32(uint value)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(_position), value);
        _position += 4;
    }

    public void U64(ulong value)
    {
        Ensure(8);
        BinaryPrimitives.WriteUInt64BigEndian(_buffer.AsSpan(_position), value);
        _position += 8;
    }

    /// <summary>Writes the minimal BOLT 1 <c>bigsize</c> encoding of <paramref name="value"/>.</summary>
    public void BigSize(ulong value)
    {
        Ensure(Domain.Protocol.Tlv.BigSizeCodec.GetLength(value));
        _position += Domain.Protocol.Tlv.BigSizeCodec.Write(value, _buffer.AsSpan(_position));
    }

    public void Bytes(ReadOnlySpan<byte> value)
    {
        Ensure(value.Length);
        value.CopyTo(_buffer.AsSpan(_position));
        _position += value.Length;
    }

    private void Ensure(int additional)
    {
        if (_position + additional <= _buffer.Length)
            return;

        var grown = ArrayPool<byte>.Shared.Rent(Math.Max(_position + additional, _buffer.Length * 2));
        _buffer.AsSpan(0, _position).CopyTo(grown);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = grown;
    }

    public readonly byte[] ToArray() => WrittenSpan.ToArray();

    /// <summary>Writes the encoded bytes to <paramref name="stream"/> without copying them out of the pooled buffer.</summary>
    public readonly void WriteTo(Stream stream) => stream.Write(WrittenSpan);

    public readonly void ReturnBuffer() => ArrayPool<byte>.Shared.Return(_buffer);
}