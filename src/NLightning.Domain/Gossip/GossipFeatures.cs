using System.Numerics;

namespace NLightning.Domain.Gossip;

using Domain.Enums;

/// <summary>
/// Feature-bit checks for gossip (BOLT 7 B7-CA-03, B7-NA-04): an unknown <b>even</b> bit in a
/// <c>channel_announcement</c> or <c>node_announcement</c> means we must not route through that channel or node.
/// </summary>
public static class GossipFeatures
{
    private static readonly HashSet<int> s_knownOddBits = [.. Enum.GetValues<Feature>().Select(f => (int)f)];

    /// <summary>
    /// True when <paramref name="bit"/> belongs to a feature this node knows (its pair's odd bit is a
    /// <see cref="Feature"/>).
    /// </summary>
    public static bool IsKnownBit(int bit) => s_knownOddBits.Contains(bit | 1);

    /// <summary>
    /// True when the big-endian wire bitmap <paramref name="wireFeatures"/> (bit 0 is the least significant bit of
    /// the last byte) sets an even bit of a feature we do not know.
    /// </summary>
    public static bool HasUnknownEvenBits(ReadOnlySpan<byte> wireFeatures)
    {
        for (var byteIndex = 0; byteIndex < wireFeatures.Length; byteIndex++)
        {
            // Even bits of each byte: 0x55
            var evenBits = wireFeatures[wireFeatures.Length - 1 - byteIndex] & 0x55;
            while (evenBits != 0)
            {
                var bitInByte = BitOperations.TrailingZeroCount(evenBits);
                evenBits &= evenBits - 1;
                if (!IsKnownBit(byteIndex * 8 + bitInByte))
                    return true;
            }
        }

        return false;
    }
}