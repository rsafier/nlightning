using System.Buffers.Binary;

namespace NLightning.Domain.Protocol.Onion.Factories;

using Enums;
using Exceptions;
using Protocol.Tlv;
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
        var typeLength = BigSizeCodec.GetLength(type.Value);
        var data = new byte[typeLength + sizeof(ushort)];

        BigSizeCodec.Write(type.Value, data);
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

        if (!BigSizeCodec.TryRead(data, out var typeValue, out var typeLength)
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
}