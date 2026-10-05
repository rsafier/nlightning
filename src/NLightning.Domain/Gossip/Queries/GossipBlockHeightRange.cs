using System.Buffers.Binary;

namespace NLightning.Domain.Gossip.Queries;

/// <summary>
/// The <c>block_height_range</c> of a <c>gossip_timestamp_filter</c> (TLV 2, taproot gossip BOLTs PR #1059, NL-878):
/// the v2 gossip (<c>channel_announcement_2</c> dated by its funding block, <c>channel_update_2</c>,
/// <c>node_announcement_2</c>) the sender wants relayed: <c>first_block_height &lt;= block_height &lt;
/// first_block_height + num_blocks</c>.
/// </summary>
/// <param name="FirstBlockHeight">The first block height the peer wants.</param>
/// <param name="NumBlocks">The length of the window.</param>
public readonly record struct GossipBlockHeightRange(uint FirstBlockHeight, uint NumBlocks)
{
    /// <summary>The range that lets nothing through (the v2 counterpart of <see cref="GossipTimestampFilter.None"/>).</summary>
    public static GossipBlockHeightRange None => new(uint.MaxValue, 0);

    /// <summary>True when <paramref name="blockHeight"/> is inside the window (computed without overflow).</summary>
    public bool Includes(uint blockHeight) =>
        blockHeight >= FirstBlockHeight && blockHeight < (ulong)FirstBlockHeight + NumBlocks;

    /// <summary>The TLV value: <c>u32 first_block_height || tu32 num_blocks</c> (minimal, big-endian).</summary>
    public byte[] Encode()
    {
        var length = NumBlocks == 0 ? 0 : 4 - (System.Numerics.BitOperations.LeadingZeroCount(NumBlocks) / 8);
        var value = new byte[4 + length];
        BinaryPrimitives.WriteUInt32BigEndian(value, FirstBlockHeight);
        for (var i = 0; i < length; i++)
            value[4 + i] = (byte)(NumBlocks >> (8 * (length - 1 - i)));
        return value;
    }

    /// <summary>
    /// Reads a TLV value; false when it is shorter than 4 bytes, its <c>tu32</c> is longer than 4 bytes or not minimal.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> value, out GossipBlockHeightRange range)
    {
        range = default;
        if (value.Length is < 4 or > 8)
            return false;

        var tail = value[4..];
        if (tail.Length > 0 && tail[0] == 0)
            return false;

        uint numBlocks = 0;
        foreach (var b in tail)
            numBlocks = (numBlocks << 8) | b;

        range = new GossipBlockHeightRange(BinaryPrimitives.ReadUInt32BigEndian(value), numBlocks);
        return true;
    }
}