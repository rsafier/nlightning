using System.Buffers.Binary;
using System.Runtime.Serialization;

namespace NLightning.Infrastructure.Serialization.Payloads;

/// <summary>
/// Big-endian field helpers shared by the BOLT 7 gossip query payload serializers.
/// </summary>
internal static class GossipQueryFieldSerializer
{
    public static async Task WriteU16PrefixedBytesAsync(ReadOnlyMemory<byte> data, Stream stream)
    {
        if (data.Length > ushort.MaxValue)
            throw new SerializationException($"Field is too long ({data.Length} bytes) for a u16 length prefix");

        var lengthBytes = new byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(lengthBytes, (ushort)data.Length);
        await stream.WriteAsync(lengthBytes);
        await stream.WriteAsync(data);
    }

    public static async Task<byte[]> ReadU16PrefixedBytesAsync(Stream stream)
    {
        var lengthBytes = new byte[sizeof(ushort)];
        await stream.ReadExactlyAsync(lengthBytes);
        var length = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);

        var data = new byte[length];
        await stream.ReadExactlyAsync(data);
        return data;
    }

    public static async Task WriteU32Async(uint value, Stream stream)
    {
        var bytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        await stream.WriteAsync(bytes);
    }

    public static async Task<uint> ReadU32Async(Stream stream)
    {
        var bytes = new byte[sizeof(uint)];
        await stream.ReadExactlyAsync(bytes);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    public static async Task WriteBoolByteAsync(bool value, Stream stream)
    {
        await stream.WriteAsync(new[] { value ? (byte)1 : (byte)0 });
    }

    public static async Task<bool> ReadBoolByteAsync(Stream stream)
    {
        var bytes = new byte[1];
        await stream.ReadExactlyAsync(bytes);
        return bytes[0] != 0;
    }
}