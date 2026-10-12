namespace NLightning.Domain.Client.Requests;

using Channels.ValueObjects;
using Payments.Enums;

/// <summary>
/// Lists our forwarded payments, newest first (<c>ClientCommand.ListForwards</c>, NL-597); every filter is optional,
/// so a plain request lists the newest page.
/// </summary>
public sealed class ListForwardsClientRequest
{
    /// <summary>How many of the newest forwards to skip.</summary>
    public int Skip { get; init; }

    /// <summary>The most forwards to return.</summary>
    public int Take { get; init; } = 100;

    /// <summary>Only forwards created at or after this time, or null for no lower bound.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Only forwards created at or before this time, or null for no upper bound.</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>Only forwards in this state, or null for all.</summary>
    public ForwardCircuitStatus? Status { get; init; }

    /// <summary>
    /// Only forwards whose incoming or outgoing channel is this one, or null for all. The <see cref="ChannelScid"/>,
    /// when also given, matches the outgoing side's requested <c>short_channel_id</c> besides.
    /// </summary>
    public ChannelId? ChannelId { get; init; }

    /// <summary>The short channel id of the <see cref="ChannelId"/> filter, when it came as one.</summary>
    public ShortChannelId? ChannelScid { get; init; }
}