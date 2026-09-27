namespace NLightning.Domain.Client.Responses;

using Channels.RoutingPolicies;
using Channels.ValueObjects;

/// <summary>
/// A channel's routing policy in force (<c>setchannelpolicy</c>/<c>getchannelpolicy</c>, wave sp1 lane SP1-G): what
/// its <c>channel_update</c> announces and our forwarding enforces, and which values come from its override (the others
/// are the node-wide <c>Node:Routing</c> values).
/// </summary>
public sealed class ChannelPolicyClientResponse
{
    public ChannelPolicyClientResponse(EffectiveChannelPolicy policy, ShortChannelId? shortChannelId)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Policy = policy;
        ShortChannelId = shortChannelId;
    }

    public EffectiveChannelPolicy Policy { get; }

    /// <summary>The channel's real short channel id, or null before its funding confirmed.</summary>
    public ShortChannelId? ShortChannelId { get; }

    /// <summary>True when the request reset the channel to the node-wide values.</summary>
    public bool WasReset { get; init; }
}