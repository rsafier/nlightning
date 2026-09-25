using System.Buffers.Binary;
using System.Numerics;

namespace NLightning.Domain.Protocol.Onion.Tlv;

using Protocol.Tlv;

/// <summary>
/// Minimal truncated-integer (tu32/tu64) encoder used by the onion TLVs to populate <see cref="BaseTlv.Value"/>.
/// </summary>
/// <remarks>
/// Big-endian with all leading zero bytes removed; zero encodes to an empty array (BOLT 1).
/// </remarks>
internal static class TruncatedIntEncoder
{
    public static byte[] Encode(ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, value);

        return buffer[(BitOperations.LeadingZeroCount(value) / 8)..].ToArray();
    }
}