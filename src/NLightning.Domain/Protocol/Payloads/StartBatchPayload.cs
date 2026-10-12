namespace NLightning.Domain.Protocol.Payloads;

using Channels.ValueObjects;
using Interfaces;

/// <summary>
/// The payload of <c>start_batch</c> (BOLT 2 "Batching channel messages", type 127): <c>channel_id</c> ‖
/// <c>u16 batch_size</c>.
/// </summary>
/// <param name="channelId">The channel every message of the batch must be for (SP-OP-04).</param>
/// <param name="batchSize">The number of messages that follow (a sender uses 2..20).</param>
public sealed class StartBatchPayload(ChannelId channelId, ushort batchSize) : IChannelMessagePayload
{
    /// <inheritdoc />
    public ChannelId ChannelId { get; } = channelId;

    /// <summary>The number of messages grouped after this one.</summary>
    public ushort BatchSize { get; } = batchSize;
}