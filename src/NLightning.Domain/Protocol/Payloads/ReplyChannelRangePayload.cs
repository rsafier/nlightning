namespace NLightning.Domain.Protocol.Payloads;

using Interfaces;
using ValueObjects;

/// <summary>
/// Represents the payload for the reply_channel_range message (BOLT 7, type 264).
/// </summary>
/// <param name="chainHash">The chain of the query being answered.</param>
/// <param name="firstBlocknum">The first block this reply covers.</param>
/// <param name="numberOfBlocks">The number of blocks this reply covers.</param>
/// <param name="syncComplete">Whether this is the final reply to the query.</param>
/// <param name="encodedShortIds">The raw <c>encoded_short_ids</c>, including the leading encoding type byte.</param>
public class ReplyChannelRangePayload(ChainHash chainHash, uint firstBlocknum, uint numberOfBlocks, bool syncComplete,
                                      ReadOnlyMemory<byte> encodedShortIds) : IMessagePayload
{
    /// <summary>
    /// The chain of the query being answered.
    /// </summary>
    public ChainHash ChainHash { get; } = chainHash;

    /// <summary>
    /// The first block this reply covers.
    /// </summary>
    public uint FirstBlocknum { get; } = firstBlocknum;

    /// <summary>
    /// The number of blocks this reply covers.
    /// </summary>
    public uint NumberOfBlocks { get; } = numberOfBlocks;

    /// <summary>
    /// The <c>sync_complete</c> byte: set on the final reply to a query.
    /// </summary>
    public bool SyncComplete { get; } = syncComplete;

    /// <summary>
    /// The raw <c>encoded_short_ids</c>: the encoding type byte followed by the encoded array.
    /// </summary>
    public ReadOnlyMemory<byte> EncodedShortIds { get; } = encodedShortIds;
}