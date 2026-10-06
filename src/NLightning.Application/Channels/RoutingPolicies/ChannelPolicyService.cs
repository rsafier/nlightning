using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Channels.RoutingPolicies;

using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.RoutingPolicies;
using Domain.Channels.ValueObjects;
using Domain.Node.Options;
using Gossip.Interfaces;

/// <inheritdoc cref="IChannelPolicyService"/>
/// <remarks>
/// <para>A change is validated (<see cref="ChannelPolicyRules.GetValidationErrors"/>) and saved
/// (<see cref="ChannelPolicyStore"/>) under the channel's lock, so no <c>channel_update</c> of the channel is signed
/// between the check and the save. From then on the forwarding policy (<c>HtlcForwardingPolicy</c>) and every new
/// <c>channel_update</c> use it: nothing needs a restart.</para>
/// <para>After the lock, a changed policy is announced at once: <see cref="IChannelUpdateService.SendChannelUpdateAsync"/>
/// signs a fresh <c>channel_update</c> (timestamp strictly above our previous one for the channel, BOLT 7) and queues
/// it to the channel peer, and, for an announced channel, hands it to the graph and the own-gossip relay, which sends
/// the newest update per channel and direction at its next flush (<c>Gossip:OwnGossipFlushInterval</c>, 60 s): a
/// burst of changes goes to the network as one update. There is no rate limit of our own on the peer path: each
/// change is sent to the peer, and peers apply theirs (LND keeps a burst of 10 updates per channel and minute), so an
/// operator should not change a channel's policy more than a few times a minute. A change that leaves the announced
/// values as they were (a patch that sets the stored values again, sets a value equal to <c>Node:Routing</c>'s, or a
/// HTLC range the channel's own limits cap anyway; a reset of a channel without an override) is saved but sends nothing
/// (BOLT 7: SHOULD NOT create redundant <c>channel_update</c>s). A channel that is not
/// <c>Open</c>, or whose update cannot be made (no short channel id yet), gets the new policy with its next update.
/// </para>
/// <para>Every call first waits for the overrides to load (<see cref="ChannelPolicyStore.LoadAsync"/>), so a database
/// failure is reported instead of answering with <c>Node:Routing</c>'s values.</para>
/// <para>Timestamps are kept in memory by the update service: after a restart the first update is stamped with the
/// clock, so several changes within the same second right before a restart can leave that update not newer than the
/// last one sent (peers then ignore it until the next change).</para>
/// </remarks>
public sealed class ChannelPolicyService : IChannelPolicyService
{
    private readonly IChannelLockProvider _channelLockProvider;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IChannelUpdateService? _channelUpdateService;
    private readonly ILogger<ChannelPolicyService> _logger;
    private readonly IOptions<NodeOptions> _nodeOptions;
    private readonly ChannelPolicyStore _store;
    private readonly TimeProvider _timeProvider;

    public ChannelPolicyService(ChannelPolicyStore store, IChannelMemoryRepository channelMemoryRepository,
                                IChannelLockProvider channelLockProvider, IOptions<NodeOptions> nodeOptions,
                                ILogger<ChannelPolicyService> logger,
                                IChannelUpdateService? channelUpdateService = null,
                                TimeProvider? timeProvider = null)
    {
        _store = store;
        _channelMemoryRepository = channelMemoryRepository;
        _channelLockProvider = channelLockProvider;
        _nodeOptions = nodeOptions;
        _logger = logger;
        _channelUpdateService = channelUpdateService;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public async Task<EffectiveChannelPolicy> SetAsync(ChannelId channelId, ChannelPolicyOverride patch,
                                                       CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (patch.ChannelId != channelId)
            throw new ArgumentException($"The policy names channel {patch.ChannelId}, not {channelId}.",
                                        nameof(patch));

        await _store.LoadAsync(cancellationToken);
        EffectiveChannelPolicy effective;
        bool changed;
        bool announced;
        using (await _channelLockProvider.AcquireAsync(channelId, cancellationToken))
        {
            var channel = GetChannel(channelId);
            var routing = _nodeOptions.Value.Routing;
            var current = _store.GetOverride(channelId);
            var before = _store.GetEffectivePolicy(channel);
            var merged = ChannelPolicyRules.Merge(current, patch, _timeProvider.GetUtcNow());

            var errors = ChannelPolicyRules.GetValidationErrors(channel, routing, _store.WithDefault(merged, channelId)!);
            if (errors.Count > 0)
                throw new ArgumentException(string.Join(" ", errors), nameof(patch));

            changed = !ChannelPolicyRules.HasSameValues(current, merged);
            if (changed)
            {
                if (ChannelPolicyRules.IsEmpty(merged))
                    await _store.DeleteAsync(channelId, cancellationToken);
                else
                    await _store.SaveAsync(merged, cancellationToken);
            }

            effective = _store.GetEffectivePolicy(channel);
            announced = !HasSameAnnouncedValues(before, effective);
        }

        if (changed)
        {
            _logger.LogInformation(
                "Routing policy of channel {ChannelId} set: base {FeeBase} msat, {FeePpm} ppm, cltv delta {Cltv}, "
              + "htlc {Min}-{Max} msat", channelId, effective.FeeBaseMsat, effective.FeeProportionalMillionths,
                effective.CltvExpiryDelta, effective.HtlcMinimumMsat, effective.HtlcMaximumMsat);
            if (announced)
                await AnnounceAsync(channelId, cancellationToken);
        }

        return effective;
    }

    /// <inheritdoc/>
    public async Task SetDefaultAsync(uint feeBaseMsat, uint feeProportionalMillionths, ushort cltvExpiryDelta,
                                      CancellationToken cancellationToken = default)
    {
        var routing = _nodeOptions.Value.Routing;
        if (cltvExpiryDelta < RoutingOptions.MinimumCltvExpiryDelta
         || cltvExpiryDelta <= routing.ExpiryTooSoonBlocks || cltvExpiryDelta > routing.MaxCltvExpiryDistance)
            throw new ArgumentException("The default CLTV delta is outside the node's routing limits.");
        using (await _channelLockProvider.AcquireAsync(ChannelId.Zero, cancellationToken))
            await _store.SaveAsync(new ChannelPolicyOverride(ChannelId.Zero, feeBaseMsat,
                                                             feeProportionalMillionths, cltvExpiryDelta,
                                                             UpdatedAt: _timeProvider.GetUtcNow()), cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<EffectiveChannelPolicy> GetAsync(ChannelId channelId,
                                                       CancellationToken cancellationToken = default)
    {
        await _store.LoadAsync(cancellationToken);
        var channel = GetChannel(channelId);
        return _store.GetEffectivePolicy(channel);
    }

    /// <inheritdoc/>
    public async Task ResetAsync(ChannelId channelId, CancellationToken cancellationToken = default)
    {
        await _store.LoadAsync(cancellationToken);
        bool removed;
        var announced = false;
        using (await _channelLockProvider.AcquireAsync(channelId, cancellationToken))
        {
            var channel = GetChannel(channelId);
            var before = _store.GetEffectivePolicy(channel);
            removed = await _store.DeleteAsync(channelId, cancellationToken);
            if (removed)
                announced = !HasSameAnnouncedValues(before, _store.GetEffectivePolicy(channel));
        }

        if (!removed)
            return;

        _logger.LogInformation("Routing policy of channel {ChannelId} reset to Node:Routing", channelId);
        if (announced)
            await AnnounceAsync(channelId, cancellationToken);
    }

    /// <summary>
    /// Sends a fresh <c>channel_update</c> with the new policy (it takes the channel's lock itself). A failure is
    /// logged: the policy is saved and applies, and the next update (reconnection, announcement) carries it.
    /// </summary>
    private async Task AnnounceAsync(ChannelId channelId, CancellationToken cancellationToken)
    {
        if (_channelUpdateService is null)
            return;

        try
        {
            await _channelUpdateService.SendChannelUpdateAsync(channelId, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Could not send the channel_update with the new routing policy of channel "
                                + "{ChannelId}; the next update will carry it", channelId);
        }
    }

    /// <summary>Whether both policies announce the same fee, CLTV delta and HTLC range (their overrides aside).</summary>
    private static bool HasSameAnnouncedValues(EffectiveChannelPolicy left, EffectiveChannelPolicy right) =>
        left.FeeBaseMsat == right.FeeBaseMsat && left.FeeProportionalMillionths == right.FeeProportionalMillionths
     && left.CltvExpiryDelta == right.CltvExpiryDelta && left.HtlcMinimumMsat == right.HtlcMinimumMsat
     && left.HtlcMaximumMsat == right.HtlcMaximumMsat;

    private ChannelModel GetChannel(ChannelId channelId) =>
        _channelMemoryRepository.TryGetChannel(channelId, out var channel) && channel is not null
            ? channel
            : throw new KeyNotFoundException($"Unknown channel {channelId}.");
}