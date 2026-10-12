namespace NLightning.Domain.Channels.Interfaces;

using RoutingPolicies;
using ValueObjects;

/// <summary>
/// Stores per-channel routing policy overrides (wave sp1 lane SP1-G; table added by lane SP1-C's migration). Writes are
/// staged on the unit of work and committed by its <c>SaveChangesAsync</c>.
/// </summary>
public interface IChannelPolicyDbRepository
{
    /// <summary>The channel's override, or null when it has none.</summary>
    Task<ChannelPolicyOverride?> GetAsync(ChannelId channelId);

    /// <summary>Every stored override.</summary>
    Task<IReadOnlyList<ChannelPolicyOverride>> GetAllAsync();

    /// <summary>Inserts or replaces the channel's override (all of its values, nulls included).</summary>
    Task UpsertAsync(ChannelPolicyOverride policyOverride);

    /// <summary>Removes the channel's override; nothing happens when there is none.</summary>
    Task DeleteAsync(ChannelId channelId);
}