namespace NLightning.Domain.Client.Requests;

using Channels.ValueObjects;
using Crypto.ValueObjects;

/// <summary>
/// Lists the channels of the gossip graph (<c>ClientCommand.ListGraphChannels</c>, BOLT 7 plan G2-T6).
/// </summary>
public sealed class ListGraphChannelsClientRequest
{
    /// <summary>Only this channel, when set.</summary>
    public ShortChannelId? ShortChannelId { get; init; }

    /// <summary>Only the channels of this node, when set.</summary>
    public CompactPubKey? NodeId { get; init; }
}