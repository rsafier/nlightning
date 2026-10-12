namespace NLightning.Domain.Protocol.Payloads;

using Bitcoin.ValueObjects;
using Channels.ValueObjects;
using Interfaces;

/// <summary>
/// The payload of <c>splice_locked</c> (BOLT 2 "Channel Splicing", type 77, SP-LK-01):
/// <c>channel_id</c> ‖ <c>sha256 splice_txid</c>.
/// </summary>
/// <param name="channelId">The spliced channel.</param>
/// <param name="spliceTxId">The splice transaction that reached acceptable depth (same byte order as
/// <c>funding_created</c>).</param>
public sealed class SpliceLockedPayload(ChannelId channelId, TxId spliceTxId) : IChannelMessagePayload
{
    /// <inheritdoc />
    public ChannelId ChannelId { get; } = channelId;

    /// <summary>The locked splice transaction id.</summary>
    public TxId SpliceTxId { get; } = spliceTxId;
}