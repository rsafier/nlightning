namespace NLightning.Domain.Protocol.Payloads;

using Interfaces;
using ValueObjects;

/// <summary>
/// Represents the payload for the query_channel_range message (BOLT 7, type 263).
/// </summary>
/// <param name="chainHash">The chain the reply must refer to.</param>
/// <param name="firstBlocknum">The first block the sender wants channels for.</param>
/// <param name="numberOfBlocks">The number of blocks, starting at <paramref name="firstBlocknum"/>.</param>
public class QueryChannelRangePayload(ChainHash chainHash, uint firstBlocknum, uint numberOfBlocks) : IMessagePayload
{
    /// <summary>
    /// The chain the reply must refer to.
    /// </summary>
    public ChainHash ChainHash { get; } = chainHash;

    /// <summary>
    /// The first block the sender wants channels for.
    /// </summary>
    public uint FirstBlocknum { get; } = firstBlocknum;

    /// <summary>
    /// The number of blocks, starting at <see cref="FirstBlocknum"/>.
    /// </summary>
    public uint NumberOfBlocks { get; } = numberOfBlocks;
}