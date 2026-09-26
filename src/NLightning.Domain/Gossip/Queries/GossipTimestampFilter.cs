namespace NLightning.Domain.Gossip.Queries;

/// <summary>
/// A <c>gossip_timestamp_filter</c> (BOLT 7, type 265): relayed gossip goes to the peer that sent it only when its
/// timestamp is greater than or equal to <see cref="FirstTimestamp"/> and less than <see cref="FirstTimestamp"/> +
/// <see cref="TimestampRange"/> (B7-Q-05). A new filter replaces the previous one.
/// </summary>
/// <param name="FirstTimestamp">The first timestamp the peer wants.</param>
/// <param name="TimestampRange">The length of the window.</param>
public readonly record struct GossipTimestampFilter(uint FirstTimestamp, uint TimestampRange)
{
    /// <summary>
    /// The filter for a peer that does not offer <c>gossip_queries</c> (BOLT 7: the sender SHOULD set
    /// <c>first_timestamp</c> to 0xFFFFFFFF and <c>timestamp_range</c> to 0, B7-Q-06): it lets nothing through.
    /// </summary>
    public static GossipTimestampFilter None => new(uint.MaxValue, 0);

    /// <summary>
    /// True when <paramref name="timestamp"/> is inside the window (<c>first &lt;= ts &lt; first + range</c>, computed
    /// without overflow).
    /// </summary>
    public bool Includes(uint timestamp) =>
        timestamp >= FirstTimestamp && timestamp < (ulong)FirstTimestamp + TimestampRange;
}