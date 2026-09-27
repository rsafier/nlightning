namespace NLightning.Application.Channels.RoutingPolicies;

using Domain.Channels.Models;
using Domain.Channels.RoutingPolicies;
using Domain.Channels.ValueObjects;

/// <summary>
/// Synchronous reads of the per-channel routing policies (wave sp1 lane SP1-G), for the paths that cannot await: the
/// forwarding policy (<c>HtlcForwardingPolicy</c>), our <c>channel_update</c> (<c>ChannelUpdateService</c>, under the
/// channel lock) and <c>listchannels</c>. Implemented by <see cref="ChannelPolicyStore"/>.
/// </summary>
public interface IChannelPolicyProvider
{
    /// <summary>The channel's stored override, or null when it has none.</summary>
    ChannelPolicyOverride? GetOverride(ChannelId channelId);

    /// <summary>The channel's configured values: its override where set, <c>Node:Routing</c> elsewhere.</summary>
    ConfiguredChannelPolicy GetConfiguredPolicy(ChannelId channelId);

    /// <summary>The policy the channel announces (<see cref="ChannelPolicyRules.Resolve"/>).</summary>
    EffectiveChannelPolicy GetEffectivePolicy(ChannelModel channel);
}