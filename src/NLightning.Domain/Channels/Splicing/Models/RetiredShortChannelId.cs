namespace NLightning.Domain.Channels.Splicing.Models;

using ValueObjects;

/// <summary>
/// A short channel id a channel stopped using when a splice locked (splicing plan D12, SP2-B-T2): the switch still
/// forwards to it until <see cref="ExpiresAtHeight"/> (the lock's height + 72 blocks, BOLT 7's forget delay, as CLN
/// #8387), so payers holding the old <c>channel_update</c> and in-flight onions keep working.
/// </summary>
/// <param name="ShortChannelId">The retired short channel id (the replaced funding's).</param>
/// <param name="ChannelId">The channel it resolves to.</param>
/// <param name="RetiredAtHeight">The height of the block that made the lock final (the splice's confirmation).</param>
/// <param name="ExpiresAtHeight">The first height at which it no longer resolves.</param>
public sealed record RetiredShortChannelId(
    ShortChannelId ShortChannelId,
    ChannelId ChannelId,
    uint RetiredAtHeight,
    uint ExpiresAtHeight)
{
    /// <summary>How long a retired short channel id keeps resolving (D12; BOLT 7 "72 blocks").</summary>
    public const uint RetentionBlocks = 72;
}