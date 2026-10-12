namespace NLightning.Domain.Protocol.Payloads;

using Interfaces;
using ValueObjects;

/// <summary>
/// Represents the payload for the gossip_timestamp_filter message (BOLT 7, type 265).
/// </summary>
/// <param name="chainHash">The chain the filter refers to.</param>
/// <param name="firstTimestamp">The first timestamp the sender wants gossip for.</param>
/// <param name="timestampRange">The length of the timestamp window.</param>
public class GossipTimestampFilterPayload(ChainHash chainHash, uint firstTimestamp, uint timestampRange)
    : IMessagePayload
{
    /// <summary>
    /// The chain the filter refers to.
    /// </summary>
    public ChainHash ChainHash { get; } = chainHash;

    /// <summary>
    /// The first timestamp the sender wants gossip for.
    /// </summary>
    public uint FirstTimestamp { get; } = firstTimestamp;

    /// <summary>
    /// The length of the timestamp window, starting at <see cref="FirstTimestamp"/>.
    /// </summary>
    public uint TimestampRange { get; } = timestampRange;
}