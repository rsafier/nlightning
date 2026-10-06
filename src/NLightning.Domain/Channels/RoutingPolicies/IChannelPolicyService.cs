namespace NLightning.Domain.Channels.RoutingPolicies;

using ValueObjects;

/// <summary>
/// Per-channel routing policy (wave sp1 lane SP1-G; IPC <c>setchannelpolicy</c> 35 and <c>getchannelpolicy</c> 36).
/// Implemented by an Application service over <see cref="Interfaces.IChannelPolicyDbRepository"/>: a change is
/// validated, saved, applied to the forwarding policy and announced with a new <c>channel_update</c> to the channel
/// peer (and, for a public channel, to the network).
/// </summary>
public interface IChannelPolicyService
{
    /// <summary>
    /// Applies <paramref name="patch"/> to the channel's override (non-null values replace the stored ones, null values
    /// keep them) and returns the policy now in force.
    /// </summary>
    /// <exception cref="ArgumentException">A value is out of range (a CLTV delta below
    /// <see cref="Node.Options.RoutingOptions.MinimumCltvExpiryDelta"/>, a minimum above the maximum, a maximum above the
    /// capacity), or the patch names another channel.</exception>
    /// <exception cref="KeyNotFoundException">Unknown channel.</exception>
    Task<EffectiveChannelPolicy> SetAsync(ChannelId channelId, ChannelPolicyOverride patch,
                                          CancellationToken cancellationToken = default);

    /// <summary>The policy in force for the channel.</summary>
    /// <exception cref="KeyNotFoundException">Unknown channel.</exception>
    Task<EffectiveChannelPolicy> GetAsync(ChannelId channelId, CancellationToken cancellationToken = default);

    /// <summary>Removes the channel's override: the node-wide values apply again (and are announced).</summary>
    /// <exception cref="KeyNotFoundException">Unknown channel.</exception>
    Task ResetAsync(ChannelId channelId, CancellationToken cancellationToken = default);
    /// <summary>Persists the fee and CLTV defaults used by channels opened later.</summary>
    Task SetDefaultAsync(uint feeBaseMsat, uint feeProportionalMillionths, ushort cltvExpiryDelta,
                         CancellationToken cancellationToken = default);
}