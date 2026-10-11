namespace NLightning.Application.Payments.Switch;

using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Splicing.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Enums;

/// <summary>
/// Which of our open channels a <c>short_channel_id</c> of an onion names: the one rule the switch's forwards (plain and
/// blinded) and the trampoline relay engine's blinded hops (NL-895) share.
/// </summary>
/// <remarks>
/// <para>One of our aliases or the peer's alias, or the real scid unless <c>option_scid_alias</c> is in the channel
/// type (<c>Compulsory</c>): BOLT 2 forbids routing into such a channel by its real scid. A channel that only negotiated
/// the feature (<c>Optional</c>, e.g. a public channel, announced by its real scid) accepts both (NL-348).</para>
/// <para>Splicing plan D12 (SP2-0 seam, lane SP2-B): a short channel id a splice lock retired resolves through
/// <see cref="IRetiredScidMap"/> for 72 blocks, after the live ones and the aliases.</para>
/// </remarks>
internal static class OutgoingChannelResolver
{
    /// <summary>The open channel <paramref name="shortChannelId"/> names, or null.</summary>
    public static ChannelModel? Resolve(IChannelMemoryRepository channels, IRetiredScidMap? retiredScidMap,
                                        ShortChannelId shortChannelId) =>
        channels.FindChannels(c => c.State == ChannelState.Open
                                && (c.LocalAliases?.Contains(shortChannelId) == true
                                 || c.RemoteAlias == shortChannelId
                                 || (c.ChannelParams.UseScidAlias != FeatureSupport.Compulsory
                                  && c.ShortChannelId != default
                                  && c.ShortChannelId == shortChannelId)))
                .FirstOrDefault()
     ?? ResolveRetired(channels, retiredScidMap, shortChannelId);

    /// <summary>The open channel a retired short channel id still names (D12), or null.</summary>
    private static ChannelModel? ResolveRetired(IChannelMemoryRepository channels, IRetiredScidMap? retiredScidMap,
                                                ShortChannelId shortChannelId) =>
        retiredScidMap is not null && retiredScidMap.TryResolve(shortChannelId, out var channelId)
     && channels.TryGetChannel(channelId, out var channel) && channel.State == ChannelState.Open
            ? channel
            : null;
}