using System.Buffers.Binary;

namespace NLightning.Domain.Protocol.Onion.Factories;

using Enums;
using Exceptions;
using Protocol.ValueObjects;

/// <summary>
/// Builds and parses the failure data of BOLT 4 <c>invalid_onion_payload</c> (PERM|22):
/// <c>[bigsize:type] [u16:offset]</c>.
/// </summary>
/// <remarks>
/// BOLT 4: "If the failure can be narrowed down to a specific tlv type in the payload, the erring node may include
/// that <c>type</c> and its byte <c>offset</c> in the decrypted byte stream." When the failure cannot be narrowed
/// down, this implementation reports type 0 and offset 0.
/// </remarks>
public static class InvalidOnionPayloadFailureFactory
{
    /// <summary>
    /// Encodes <c>bigsize type || u16 offset</c>. Offsets outside 0..65535 are clamped.
    /// </summary>
    public static byte[] EncodeData(BigSize type, int offset)
    {
        var clampedOffset = (ushort)Math.Clamp(offset, 0, ushort.MaxValue);
        var typeLength = GetBigSizeLength(type.Value);
        var data = new byte[typeLength + sizeof(ushort)];

        WriteBigSize(type.Value, data.AsSpan(0, typeLength));
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(typeLength), clampedOffset);

        return data;
    }

    /// <summary>
    /// Decodes <c>bigsize type || u16 offset</c>. Trailing bytes are ignored (BOLT 4: the origin MUST ignore them).
    /// </summary>
    /// <returns><c>false</c> when the data is truncated or the bigsize is not minimally encoded.</returns>
    public static bool TryDecodeData(ReadOnlySpan<byte> data, out BigSize type, out ushort offset)
    {
        type = default;
        offset = 0;

        if (!TryReadBigSize(data, out var typeValue, out var typeLength)
         || data.Length - typeLength < sizeof(ushort))
            return false;

        type = new BigSize(typeValue);
        offset = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(typeLength, sizeof(ushort)));
        return true;
    }

    /// <summary>
    /// Creates an <see cref="OnionException"/> with <see cref="FailureCode.InvalidOnionPayload"/> and the encoded
    /// <c>type || offset</c> data.
    /// </summary>
    public static OnionException Create(BigSize type, int offset, string message, Exception? innerException = null)
    {
        var data = EncodeData(type, offset);

        return innerException is null
                   ? new OnionException(FailureCode.InvalidOnionPayload, message, data)
                   : new OnionException(FailureCode.InvalidOnionPayload, message, innerException, data);
    }

    private static int GetBigSizeLength(ulong value)
    {
        return value switch
        {
            < 0xfd => 1,
            <= ushort.MaxValue => 3,
            <= uint.MaxValue => 5,
            _ => 9
        };
    }

    private static void WriteBigSize(ulong value, Span<byte> destination)
    {
        switch (destination.Length)
        {
            case 1:
                destination[0] = (byte)value;
                break;
            case 3:
                destination[0] = 0xfd;
                BinaryPrimitives.WriteUInt16BigEndian(destination[1..], (ushort)value);
                break;
            case 5:
                destination[0] = 0xfe;
                BinaryPrimitives.WriteUInt32BigEndian(destination[1..], (uint)value);
                break;
            default:
                destination[0] = 0xff;
                BinaryPrimitives.WriteUInt64BigEndian(destination[1..], value);
                break;
        }
    }

    private static bool TryReadBigSize(ReadOnlySpan<byte> data, out ulong value, out int length)
    {
        value = 0;
        length = 0;

        if (data.IsEmpty)
            return false;

        switch (data[0])
        {
            case < 0xfd:
                value = data[0];
                length = 1;
                return true;
            case 0xfd:
                if (data.Length < 3)
                    return false;

                value = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(1, 2));
                length = 3;
                return value >= 0xfd;
            case 0xfe:
                if (data.Length < 5)
                    return false;

                value = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(1, 4));
                length = 5;
                return value > ushort.MaxValue;
            default:
                if (data.Length < 9)
                    return false;

                value = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(1, 8));
                length = 9;
                return value > uint.MaxValue;
        }
    }
}