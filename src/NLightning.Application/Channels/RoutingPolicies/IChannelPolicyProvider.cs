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

    /// <summary>
    /// The configured values each change of the channel's policy replaced within the last
    /// <see cref="ChannelPolicyStore.PreviousPolicyGracePeriod"/> (newest first; empty when there is none). BOLT 7:
    /// after a new <c>channel_update</c> the origin node SHOULD keep accepting the previous parameters for 10 minutes
    /// ("HTLC Fees": HTLCs that pay an older fee, for the propagation delay), so the forwarding policy accepts an HTLC
    /// whose fee and <c>cltv_expiry_delta</c> satisfy any of them.
    /// </summary>
    IReadOnlyList<ConfiguredChannelPolicy> GetPreviousPolicies(ChannelId channelId);

    /// <summary>
    /// Whether the stored overrides are loaded. While false (the load failed) the reads above return the
    /// <c>Node:Routing</c> values, which need not be the channel's policy: the forwarding policy refuses HTLCs and no
    /// <c>channel_update</c> is made until a load succeeds. Read it after one of the reads above (they try the load).
    /// </summary>
    bool IsLoaded { get; }

    /// <summary>False when overrides are kept in memory only (forgotten on restart).</summary>
    bool IsPersistent { get; }
}