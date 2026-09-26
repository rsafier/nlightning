using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Gossip.Services;

using Announcements;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Events;
using Interfaces;

/// <inheritdoc cref="IChannelUpdateService"/>
/// <remarks>
/// <para>
/// Sending: the service listens to <see cref="IChannelMemoryRepository.OnChannelUpdated"/>. The first time a channel
/// is seen <c>Open</c> with a short channel id, it builds our update on another task that first takes the channel's
/// lock. That event is raised while the handler that opened the channel still holds the lock and before it queues
/// its own <c>channel_ready</c>, so waiting for the lock puts our update after that <c>channel_ready</c> in the peer's
/// outbox.
/// </para>
/// <para>
/// Fields (BOLT 7): <c>must_be_one</c>; <c>dont_forward</c> unless the channel is announced (public, both halves of
/// <c>announcement_signatures</c> exchanged: <see cref="IsPublic"/>); <c>direction</c> = 1 when our node id is the
/// greater one; the real short channel id (for an unannounced <c>option_scid_alias</c> channel the alias the peer sent
/// us instead: BOLT 2 forbids routing into it by the real one; an announced channel always uses the real one, which
/// its <c>channel_announcement</c> names); the fee and CLTV delta of
/// <c>NodeOptions.Routing</c>, <c>htlc_minimum_msat</c> = the larger of the peer's <c>htlc_minimum_msat</c> and
/// <c>Routing.HtlcMinimumMsat</c>, <c>htlc_maximum_msat</c> = the smallest of the capacity, the peer's
/// <c>max_htlc_value_in_flight_msat</c> and <c>Routing.HtlcMaximumMsat</c>. No update is made when the minimum is
/// above that maximum (BOLT 7: the maximum must not exceed the capacity) or an alias channel has no peer alias yet.
/// </para>
/// <para>
/// Receiving: the peer's update must be for our chain, a channel we have with it (real scid or an alias), its own
/// direction, carry its node signature, be newer than the last one kept and no more than
/// <see cref="MaxFutureTimestamp"/> ahead of our clock, and have <c>htlc_maximum_msat</c> within the capacity.
/// </para>
/// <para>
/// Resending: each new connection to a peer (after init) gets our update for every <c>Open</c> channel with it
/// (<see cref="SendChannelUpdatesToPeerAsync"/>, called by the peer manager), so the peer learns a policy that changed
/// while it was disconnected or we were down. An unchanged policy goes out as the same message (same timestamp), as
/// LND does on reconnect: LND ignores a same-policy "keep-alive" update younger than 24 h.
/// </para>
/// <para>
/// Public mode (BOLT 7 plan G1-T5): once a channel is announced (<see cref="OnChannelAnnounced"/>) a new update is
/// signed with <c>dont_forward</c> clear and sent to the peer, and every update made for an announced channel is also
/// handed to the graph and the relay (<see cref="OwnGossipPublisher"/>). When an announced channel starts closing
/// (shutdown, closing, failed) a <c>disable</c>d update is made once and handed to the relay only.
/// </para>
/// <para>
/// Everything is in memory: the peer's update is forgotten on restart until it sends a new one.
/// </para>
/// </remarks>
public sealed class ChannelUpdateService : IChannelUpdateService, IDisposable
{
    /// <summary>
    /// How far ahead of our clock a peer's <c>timestamp</c> may be (BOLT 7: MAY discard one unreasonably far in the
    /// future). Without it, one update near <c>uint.MaxValue</c> would shadow every later real one.
    /// </summary>
    internal static readonly TimeSpan MaxFutureTimestamp = TimeSpan.FromDays(14);

    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IChannelLockProvider _channelLockProvider;
    private readonly ILightningSigner _lightningSigner;
    private readonly ISecureKeyManager _secureKeyManager;
    private readonly ILogger<ChannelUpdateService> _logger;
    private readonly NodeOptions _nodeOptions;
    private readonly TimeProvider _timeProvider;
    private readonly OwnGossipPublisher? _ownGossipPublisher;

    private readonly ConcurrentDictionary<ChannelId, byte> _sentOnOpen = new();
    private readonly ConcurrentDictionary<ChannelId, ChannelUpdateMessage> _localUpdates = new();
    private readonly ConcurrentDictionary<ChannelId, ChannelUpdatePayload> _remoteUpdates = new();
    private readonly ConcurrentDictionary<ChannelId, uint> _lastLocalTimestamps = new();
    private readonly ConcurrentDictionary<ChannelId, byte> _disabledOnClose = new();

    /// <inheritdoc/>
    public event EventHandler<ChannelUpdateReadyEventArgs>? OnChannelUpdateReady;

    public ChannelUpdateService(IChannelMemoryRepository channelMemoryRepository,
                                IChannelLockProvider channelLockProvider, ILightningSigner lightningSigner,
                                ISecureKeyManager secureKeyManager, IOptions<NodeOptions> nodeOptions,
                                ILogger<ChannelUpdateService> logger, TimeProvider? timeProvider = null,
                                OwnGossipPublisher? ownGossipPublisher = null)
    {
        _channelMemoryRepository = channelMemoryRepository;
        _channelLockProvider = channelLockProvider;
        _lightningSigner = lightningSigner;
        _secureKeyManager = secureKeyManager;
        _logger = logger;
        _nodeOptions = nodeOptions.Value;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ownGossipPublisher = ownGossipPublisher;

        _channelMemoryRepository.OnChannelUpdated += HandleChannelUpdated;
    }

    /// <inheritdoc/>
    public ChannelUpdateMessage CreateChannelUpdate(ChannelModel channel, bool disabled = false)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!TryGetPolicy(channel, out var policy, out var reason))
            throw new InvalidOperationException($"No channel_update for channel {channel.ChannelId}: {reason}");

        var unsigned = BuildUnsignedUpdate(channel, policy, disabled, NextTimestamp(channel.ChannelId));
        var signature = _lightningSigner.SignNodeMessage(unsigned.GetSignatureHash());
        var message = new ChannelUpdateMessage(unsigned.WithSignature(signature));

        _localUpdates[channel.ChannelId] = message;

        // BOLT 7: only the update of an announced channel may be forwarded (dont_forward clear)
        if (IsPublic(channel))
            _ownGossipPublisher?.PublishChannelUpdate(message.Payload);
        return message;
    }

    /// <inheritdoc/>
    public ChannelUpdateMessage? OnChannelAnnounced(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (channel.State != ChannelState.Open || !IsPublic(channel))
            return null;

        if (!TryGetPolicy(channel, out _, out var reason))
        {
            _logger.LogWarning("No public channel_update for announced channel {ChannelId}: {Reason}",
                               channel.ChannelId, reason);
            return null;
        }

        // The update made at the open (if any) had dont_forward set; this one replaces it everywhere
        _sentOnOpen.TryAdd(channel.ChannelId, 0);
        var message = CreateChannelUpdate(channel);
        _logger.LogInformation("Sending the public channel_update of announced channel {ChannelId} ({ShortChannelId})",
                               channel.ChannelId, channel.ShortChannelId);

        // Only enqueues (the caller holds the channel lock)
        OnChannelUpdateReady?.Invoke(this, new ChannelUpdateReadyEventArgs(channel.RemoteNodeId, message));
        return message;
    }

    /// <summary>
    /// Whether our update for the channel is public (BOLT 7): the channel has <c>announce_channel</c>, its real short
    /// channel id, and both halves of <c>announcement_signatures</c> were exchanged (ours only goes out at the
    /// announcement depth). Before that, and for a private channel, <c>dont_forward</c> is set.
    /// </summary>
    public static bool IsPublic(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return channel.AnnounceChannel && ((byte[]?)channel.ShortChannelId) is not null
            && channel.RemoteAnnouncementSignatures is not null
            && channel.LocalAnnouncementSignaturesSentAt is not null;
    }

    /// <inheritdoc/>
    public Task SendChannelUpdateAsync(ChannelId channelId, CancellationToken cancellationToken = default)
    {
        return SendChannelUpdateAsync(channelId, reuseUnchanged: false, cancellationToken);
    }

    /// <summary>
    /// With <paramref name="reuseUnchanged"/>, our last update for the channel is sent again as is when nothing but
    /// its timestamp would change (as LND does on reconnect): peers ignore a same-policy "keep-alive" update younger
    /// than a day anyway, and this spends none of their per-channel update rate limit.
    /// </summary>
    private async Task SendChannelUpdateAsync(ChannelId channelId, bool reuseUnchanged,
                                              CancellationToken cancellationToken)
    {
        using var channelLock = await _channelLockProvider.AcquireAsync(channelId, cancellationToken);

        if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel) || channel.State != ChannelState.Open
         || channel.ShortChannelId == default || channel.FundingOutput is null)
        {
            _logger.LogDebug("Not sending a channel_update for channel {ChannelId}: it is not open", channelId);
            return;
        }

        if (!TryGetPolicy(channel, out var policy, out var reason))
        {
            _logger.LogWarning("Not sending a channel_update for channel {ChannelId}: {Reason}", channelId, reason);
            return;
        }

        var message = reuseUnchanged && _localUpdates.TryGetValue(channelId, out var last)
                                     && IsCurrent(channel, policy, last.Payload)
                          ? last
                          : CreateChannelUpdate(channel);
        _logger.LogInformation("Sending channel_update for channel {ChannelId} ({ShortChannelId}) to peer {Peer}",
                               channelId, channel.ShortChannelId, channel.RemoteNodeId);

        // Only enqueues (we hold the channel lock)
        OnChannelUpdateReady?.Invoke(this, new ChannelUpdateReadyEventArgs(channel.RemoteNodeId, message));
    }

    /// <inheritdoc/>
    public async Task SendChannelUpdatesToPeerAsync(CompactPubKey peerPubKey,
                                                    CancellationToken cancellationToken = default)
    {
        var channelIds = _channelMemoryRepository
                        .FindChannels(c => c.RemoteNodeId == peerPubKey && c.State == ChannelState.Open)
                        .Select(c => c.ChannelId)
                        .ToList();

        foreach (var channelId in channelIds)
        {
            // Opening it now would send it again
            _sentOnOpen.TryAdd(channelId, 0);
            await SendChannelUpdateAsync(channelId, reuseUnchanged: true, cancellationToken);
        }
    }

    /// <inheritdoc/>
    public bool HandleRemoteChannelUpdate(CompactPubKey peerPubKey, ChannelUpdateMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var update = message.Payload;

        // BOLT 7: MUST ignore an update for a chain we don't know
        if (update.ChainHash != _nodeOptions.BitcoinNetwork.ChainHash)
            return Ignore(peerPubKey, update, "it is for another chain");

        var channel = FindChannel(peerPubKey, update.ShortChannelId);
        if (channel is null)
            return Ignore(peerPubKey, update, "we have no such channel with that peer");

        // Only the peer's own direction: an update in our direction would claim to be ours
        if (update.Direction != IsNode2(peerPubKey, _secureKeyManager.GetNodePubKey()))
            return Ignore(peerPubKey, update, "its direction is not the peer's");

        if (!_lightningSigner.VerifyNodeMessage(update.GetSignatureHash(), update.Signature, peerPubKey))
            return Ignore(peerPubKey, update, "its signature is invalid");

        // BOLT 7: MAY discard a timestamp unreasonably far in the future (it would shadow every later update)
        var latestAccepted = _timeProvider.GetUtcNow().Add(MaxFutureTimestamp).ToUnixTimeSeconds();
        if (update.Timestamp > latestAccepted)
            return Ignore(peerPubKey, update, "its timestamp is too far in the future");

        // BOLT 7: SHOULD ignore the channel for routing when htlc_maximum_msat is above the capacity
        if (channel.FundingOutput is { } fundingOutput
         && update.HtlcMaximumMsat > fundingOutput.Amount.MilliSatoshi)
            return Ignore(peerPubKey, update, "its htlc_maximum_msat is above the channel capacity");

        while (true)
        {
            if (_remoteUpdates.TryGetValue(channel.ChannelId, out var current))
            {
                // BOLT 7: SHOULD ignore an update whose timestamp is not greater than the last one
                if (update.Timestamp <= current.Timestamp)
                    return Ignore(peerPubKey, update, "it is not newer than the one we have");

                if (_remoteUpdates.TryUpdate(channel.ChannelId, update, current))
                    break;
            }
            else if (_remoteUpdates.TryAdd(channel.ChannelId, update))
            {
                break;
            }
        }

        _logger.LogInformation(
            "Stored channel_update of peer {Peer} for channel {ChannelId}: base {FeeBase} msat, {FeePpm} ppm, "
          + "cltv delta {CltvDelta}{Disabled}", peerPubKey, channel.ChannelId, update.FeeBaseMsat,
            update.FeeProportionalMillionths, update.CltvExpiryDelta, update.IsDisabled ? ", disabled" : "");
        return true;
    }

    /// <inheritdoc/>
    public bool TryGetRemoteChannelUpdate(ChannelId channelId, out ChannelUpdatePayload? update)
    {
        return _remoteUpdates.TryGetValue(channelId, out update);
    }

    /// <inheritdoc/>
    public bool TryGetLocalChannelUpdate(ChannelId channelId, out ChannelUpdateMessage? update)
    {
        return _localUpdates.TryGetValue(channelId, out update);
    }

    public void Dispose()
    {
        _channelMemoryRepository.OnChannelUpdated -= HandleChannelUpdated;
    }

    /// <summary>
    /// Runs while the caller holds the channel's lock: only schedules the send, which waits for that lock.
    /// </summary>
    private void HandleChannelUpdated(object? sender, ChannelUpdatedEventArgs? args)
    {
        if (args?.Channel is not { } channel)
            return;

        if (channel.State is ChannelState.ShuttingDown or ChannelState.Negotiating or ChannelState.Closing
                          or ChannelState.Failed)
        {
            DisableClosingPublicChannel(channel);
            return;
        }

        if (channel.State != ChannelState.Open || channel.ShortChannelId == default
                                               || !_sentOnOpen.TryAdd(channel.ChannelId, 0))
            return;

        var channelId = channel.ChannelId;
        _ = Task.Run(async () =>
        {
            try
            {
                await SendChannelUpdateAsync(channelId);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to send the channel_update for channel {ChannelId}", channelId);
            }
        });
    }

    /// <summary>
    /// An announced channel that starts closing: the rest of the network learns that it is no longer usable (BOLT 7:
    /// MAY send a <c>disable</c>d update prior to an on-chain settlement). Once per channel, to the relay only (the
    /// peer knows). Runs under the channel's lock.
    /// </summary>
    private void DisableClosingPublicChannel(ChannelModel channel)
    {
        if (_ownGossipPublisher is null || !IsPublic(channel) || !_disabledOnClose.TryAdd(channel.ChannelId, 0))
            return;

        try
        {
            if (!TryGetPolicy(channel, out _, out _))
                return;

            CreateChannelUpdate(channel, disabled: true);
            _logger.LogInformation("Announced channel {ChannelId} is closing: its channel_update is now disabled",
                                   channel.ChannelId);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not make the disabled channel_update of closing channel {ChannelId}",
                               channel.ChannelId);
        }
    }

    /// <summary>
    /// The channel-dependent fields of our update (rules in the class remarks), or why the channel can't have one.
    /// </summary>
    private bool TryGetPolicy(ChannelModel channel, out UpdatePolicy policy, out string reason)
    {
        policy = default;

        ShortChannelId shortChannelId;
        if (IsPublic(channel))
        {
            // BOLT 7: the announced channel is named by its real short channel id, alias or not
            shortChannelId = channel.ShortChannelId;
        }
        else if (channel.ChannelParams.UseScidAlias > FeatureSupport.No)
        {
            // BOLT 2: MUST NOT allow incoming HTLCs to an option_scid_alias channel by its real short_channel_id
            if (channel.RemoteAlias is not { } remoteAlias)
            {
                reason = "it is an option_scid_alias channel and the peer sent no alias yet";
                return false;
            }

            shortChannelId = remoteAlias;
        }
        else if (channel.ShortChannelId == default)
        {
            reason = "it has no short channel id yet";
            return false;
        }
        else
        {
            shortChannelId = channel.ShortChannelId;
        }

        if (channel.FundingOutput is null)
        {
            reason = "it has no funding output";
            return false;
        }

        var capacityMsat = channel.FundingOutput.Amount.MilliSatoshi;
        var routing = _nodeOptions.Routing;
        var htlcMinimumMsat = Math.Max(channel.ChannelParams.Remote.HtlcMinimumAmount.MilliSatoshi,
                                       routing.HtlcMinimumMsat);
        var htlcMaximumMsat = capacityMsat;
        var remoteMaxInFlight = channel.ChannelParams.Remote.MaxHtlcValueInFlight.MilliSatoshi;
        if (remoteMaxInFlight > 0)
            htlcMaximumMsat = Math.Min(htlcMaximumMsat, remoteMaxInFlight);
        if (routing.HtlcMaximumMsat is { } configuredMaximum)
            htlcMaximumMsat = Math.Min(htlcMaximumMsat, configuredMaximum);

        // BOLT 7: htlc_maximum_msat MUST NOT exceed the capacity, so it can't be raised to the minimum
        if (htlcMinimumMsat > htlcMaximumMsat)
        {
            reason = $"htlc_minimum_msat {htlcMinimumMsat} is above the largest HTLC it can carry ({htlcMaximumMsat} "
                   + "msat: capacity, the peer's max_htlc_value_in_flight_msat, Routing.HtlcMaximumMsat)";
            return false;
        }

        policy = new UpdatePolicy(shortChannelId, htlcMinimumMsat, htlcMaximumMsat);
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Our (unsigned) update for the channel with the current routing options (field rules in the class remarks).
    /// </summary>
    private ChannelUpdatePayload BuildUnsignedUpdate(ChannelModel channel, UpdatePolicy policy, bool disabled,
                                                     uint timestamp)
    {
        var routing = _nodeOptions.Routing;
        var channelFlags = IsNode2(_secureKeyManager.GetNodePubKey(), channel.RemoteNodeId)
                               ? ChannelUpdatePayload.ChannelFlagDirection
                               : (byte)0;
        if (disabled)
            channelFlags |= ChannelUpdatePayload.ChannelFlagDisable;

        // BOLT 7: dont_forward until the channel is announced
        var messageFlags = IsPublic(channel)
                               ? ChannelUpdatePayload.MessageFlagMustBeOne
                               : (byte)(ChannelUpdatePayload.MessageFlagMustBeOne
                                      | ChannelUpdatePayload.MessageFlagDontForward);
        return new ChannelUpdatePayload(ChannelUpdatePayload.EmptySignature, _nodeOptions.BitcoinNetwork.ChainHash,
                                        policy.ShortChannelId, timestamp, messageFlags, channelFlags,
                                        routing.CltvExpiryDelta, policy.HtlcMinimumMsat, routing.FeeBaseMsat,
                                        routing.FeeProportionalMillionths, policy.HtlcMaximumMsat);
    }

    /// <summary>
    /// Whether <paramref name="last"/> still says what a new, enabled update would (all but timestamp and signature).
    /// </summary>
    private bool IsCurrent(ChannelModel channel, UpdatePolicy policy, ChannelUpdatePayload last)
    {
        var current = BuildUnsignedUpdate(channel, policy, disabled: false, last.Timestamp);
        return current.ChainHash == last.ChainHash && current.ShortChannelId == last.ShortChannelId
            && current.MessageFlags == last.MessageFlags && current.ChannelFlags == last.ChannelFlags
            && current.CltvExpiryDelta == last.CltvExpiryDelta && current.HtlcMinimumMsat == last.HtlcMinimumMsat
            && current.FeeBaseMsat == last.FeeBaseMsat
            && current.FeeProportionalMillionths == last.FeeProportionalMillionths
            && current.HtlcMaximumMsat == last.HtlcMaximumMsat;
    }

    private ChannelModel? FindChannel(CompactPubKey peerPubKey, ShortChannelId shortChannelId)
    {
        // The peer may name the channel by its real scid or by an alias (ours or its own)
        return _channelMemoryRepository
              .FindChannels(c => c.RemoteNodeId == peerPubKey
                              && (c.ShortChannelId == shortChannelId || c.RemoteAlias == shortChannelId
                               || (c.LocalAliases?.Contains(shortChannelId) ?? false)))
              .FirstOrDefault();
    }

    private bool Ignore(CompactPubKey peerPubKey, ChannelUpdatePayload update, string reason)
    {
        _logger.LogDebug("Ignoring channel_update for {ShortChannelId} from peer {Peer}: {Reason}",
                         update.ShortChannelId, peerPubKey, reason);
        return false;
    }

    /// <summary>
    /// UNIX time, but always after the last update we made for the channel (BOLT 7: timestamps must increase).
    /// </summary>
    private uint NextTimestamp(ChannelId channelId)
    {
        var now = (uint)Math.Clamp(_timeProvider.GetUtcNow().ToUnixTimeSeconds(), 0, uint.MaxValue);
        return _lastLocalTimestamps.AddOrUpdate(channelId, now, (_, last) => Math.Max(now, last + 1));
    }

    /// <summary>
    /// Whether <paramref name="origin"/> is <c>node_id_2</c> (the greater compressed key) of the channel with
    /// <paramref name="other"/> (BOLT 7 <c>direction</c> bit).
    /// </summary>
    private static bool IsNode2(CompactPubKey origin, CompactPubKey other)
    {
        return ((ReadOnlySpan<byte>)origin).SequenceCompareTo(other) > 0;
    }

    /// <summary>
    /// The short channel id and HTLC limits our update for a channel carries.
    /// </summary>
    private readonly record struct UpdatePolicy(ShortChannelId ShortChannelId, ulong HtlcMinimumMsat,
                                                ulong HtlcMaximumMsat);
}