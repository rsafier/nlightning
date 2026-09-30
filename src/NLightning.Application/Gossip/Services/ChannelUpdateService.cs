using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Gossip.Services;

using Announcements;
using Channels.Interfaces;
using Channels.RoutingPolicies;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
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
/// its <c>channel_announcement</c> names); the channel's routing policy (wave sp1 lane SP1-G,
/// <see cref="ChannelPolicyRules.Resolve"/>: its <c>setchannelpolicy</c> override where set, <c>NodeOptions.Routing</c>
/// elsewhere, read from the optional <see cref="IChannelPolicyProvider"/> at every update): fee and CLTV delta,
/// <c>htlc_minimum_msat</c> = the larger of the peer's <c>htlc_minimum_msat</c> and the configured minimum,
/// <c>htlc_maximum_msat</c> = the smallest of the capacity, the peer's <c>max_htlc_value_in_flight_msat</c> and the
/// configured maximum. No update is made when the minimum is above that maximum (BOLT 7: the maximum must not exceed
/// the capacity), an alias channel has no peer alias yet, or the provider could not load the policy overrides
/// (<see cref="IChannelPolicyProvider.IsLoaded"/>: <c>Node:Routing</c> would stand in for them). A policy change is announced by the policy service through
/// <see cref="SendChannelUpdateAsync(ChannelId, CancellationToken)"/> (a fresh update, to the peer and, when the channel
/// is announced, to the relay).
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
/// Offline peers (NL-349, plan G1-T5): with an <see cref="IPeerLivenessProbe"/> (resolved lazily from the service
/// provider) and a positive <see cref="GossipOptions.DisableAfter"/>, a timer checks every
/// <see cref="GetOfflineCheckInterval"/> whether the link of each open announced channel is up. A channel whose link
/// stayed down for <c>DisableAfter</c> (20 min) gets one <c>disable</c>d update, handed to the relay only (the peer is
/// away). It is enabled again with a newer update, to the peer and the relay, when the probe raises its link-up hook
/// (<c>MarkLinkUp</c>: the channel turned Open or was reestablished on the peer's current connection; NL-364) — no
/// longer only at the next check, which could be a minute away. The peer's next connection
/// (<see cref="SendChannelUpdatesToPeerAsync"/>) gets the disabled update as is, so a reestablish that fails keeps
/// the channel disabled. The disable itself is decided again under the channel's lock (same offline start, link still
/// down), so a reconnection racing a check never relays a disabled update for a link that is back.
/// The offline time is counted from the first check that finds the link down — seeded, through the peers' table, from
/// the peer's persisted last-seen time (its row moves while its pongs come), so a restart does not count a peer's
/// absence from zero (NL-364).
/// </para>
/// <para>
/// Short channel id switch (splicing plan D12, SP2-B-T2): the service remembers the short channel id of each open
/// channel it sees; when it moves after our first update (a splice lock, or a reorg) a new update for the new one goes
/// to the peer (<see cref="ScheduleShortChannelIdSwitch"/>). The old one's update is left alone.
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
    private readonly IChannelPolicyProvider? _channelPolicyProvider;

    private readonly ConcurrentDictionary<ChannelId, byte> _sentOnOpen = new();
    private readonly ConcurrentDictionary<ChannelId, ShortChannelId> _knownShortChannelIds = new();
    private readonly ConcurrentDictionary<ChannelId, ChannelUpdateMessage> _localUpdates = new();
    private readonly ConcurrentDictionary<ChannelId, ChannelUpdatePayload> _remoteUpdates = new();
    private readonly ConcurrentDictionary<ChannelId, uint> _lastLocalTimestamps = new();
    private readonly ConcurrentDictionary<ChannelId, byte> _disabledOnClose = new();
    private readonly ConcurrentDictionary<ChannelId, DateTimeOffset> _offlineSince = new();
    private readonly ConcurrentDictionary<ChannelId, byte> _disabledOffline = new();
    private readonly IServiceProvider? _serviceProvider;
    private readonly TimeSpan _disableAfter;
    private readonly ITimer? _offlineCheckTimer;
    private int _offlineCheckRunning;
    private IPeerLivenessProbe? _probe;

    /// <inheritdoc/>
    public event EventHandler<ChannelUpdateReadyEventArgs>? OnChannelUpdateReady;

    public ChannelUpdateService(IChannelMemoryRepository channelMemoryRepository,
                                IChannelLockProvider channelLockProvider, ILightningSigner lightningSigner,
                                ISecureKeyManager secureKeyManager, IOptions<NodeOptions> nodeOptions,
                                ILogger<ChannelUpdateService> logger, TimeProvider? timeProvider = null,
                                OwnGossipPublisher? ownGossipPublisher = null,
                                IOptions<GossipOptions>? gossipOptions = null,
                                IServiceProvider? serviceProvider = null,
                                IChannelPolicyProvider? channelPolicyProvider = null)
    {
        _channelMemoryRepository = channelMemoryRepository;
        _channelLockProvider = channelLockProvider;
        _lightningSigner = lightningSigner;
        _secureKeyManager = secureKeyManager;
        _logger = logger;
        _nodeOptions = nodeOptions.Value;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ownGossipPublisher = ownGossipPublisher;
        _serviceProvider = serviceProvider;
        _channelPolicyProvider = channelPolicyProvider;
        _disableAfter = (gossipOptions?.Value ?? new GossipOptions()).DisableAfter;

        _channelMemoryRepository.OnChannelUpdated += HandleChannelUpdated;

        // Only a node that can tell whether a link is up (the daemon) disables the channels of offline peers. The
        // checks run on the system clock; the offline time is measured with the injected one (a test clock's timers
        // stay the test's own)
        if (serviceProvider is not null && _disableAfter > TimeSpan.Zero)
        {
            var interval = GetOfflineCheckInterval(_disableAfter);
            _offlineCheckTimer = TimeProvider.System.CreateTimer(_ => StartOfflineCheck(), null, interval, interval);
        }
    }

    /// <summary>The last offline check the timer started (tests).</summary>
    internal Task LastOfflineCheck { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The liveness probe, resolved once from the service provider (never present in the in-process tests without
    /// one); its link-up hook (NL-364) is subscribed at the same time, so nothing is missed that could matter: before
    /// the first check no channel is disabled, so there is nothing to re-enable.
    /// </summary>
    private IPeerLivenessProbe? Probe
    {
        get
        {
            if (_probe is not null)
                return _probe;

            if (_serviceProvider?.GetService<IPeerLivenessProbe>() is not { } probe)
                return null;

            _probe = probe;
            probe.LinkUp += HandleLinkUp;
            return probe;
        }
    }

    /// <summary>
    /// How often the links of announced channels are checked: a quarter of <paramref name="disableAfter"/>, between 1 s
    /// and 1 min, so a channel is disabled at most a minute late.
    /// </summary>
    internal static TimeSpan GetOfflineCheckInterval(TimeSpan disableAfter) =>
        TimeSpan.FromTicks(Math.Clamp(disableAfter.Ticks / 4, TimeSpan.TicksPerSecond, TimeSpan.TicksPerMinute));

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
    public ChannelUpdateMessage? CreateChannelUpdateForScid(ChannelModel channel, ShortChannelId shortChannelId)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!NamesChannel(channel, shortChannelId)
         || !TryGetPolicy(channel, out var policy, out _, shortChannelId))
            return null;

        var unsigned = BuildUnsignedUpdate(channel, policy, disabled: false, NextTimestamp(channel.ChannelId));
        var signature = _lightningSigner.SignNodeMessage(unsigned.GetSignatureHash());
        return new ChannelUpdateMessage(unsigned.WithSignature(signature));
    }

    /// <summary>Whether <paramref name="shortChannelId"/> is one of the names <paramref name="channel"/> goes by: its
    /// real short channel id, the alias the peer sent us, or one of the aliases we sent it.</summary>
    private static bool NamesChannel(ChannelModel channel, ShortChannelId shortChannelId) =>
        (channel.ShortChannelId != default && channel.ShortChannelId == shortChannelId)
     || channel.RemoteAlias == shortChannelId
     || (channel.LocalAliases?.Contains(shortChannelId) ?? false);

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

        // A channel disabled while its peer was away stays disabled until a check finds its link up (NL-349)
        var disabled = _disabledOffline.ContainsKey(channelId);
        var message = reuseUnchanged && _localUpdates.TryGetValue(channelId, out var last)
                                     && IsCurrent(channel, policy, last.Payload, disabled)
                          ? last
                          : CreateChannelUpdate(channel, disabled);
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

            // The peer is back: the offline time starts again. A channel disabled while it was away stays disabled
            // (the peer gets that update as is) until a check finds its link up after the reestablish, so a
            // reestablish that fails never advertises it enabled (NL-349)
            _offlineSince.TryRemove(channelId, out _);
            await SendChannelUpdateAsync(channelId, reuseUnchanged: true, cancellationToken);
        }
    }

    /// <summary>
    /// One offline check (NL-349): every open announced channel whose link has been down for
    /// <see cref="GossipOptions.DisableAfter"/> gets a disabled update (relay only, once until it is enabled again);
    /// one found up again after that gets a newer enabled update. Does nothing without an
    /// <see cref="IPeerLivenessProbe"/>.
    /// </summary>
    internal async Task CheckOfflinePeersAsync(CancellationToken cancellationToken = default)
    {
        if (_disableAfter <= TimeSpan.Zero || Probe is not { } probe)
            return;

        var channels = _channelMemoryRepository.FindChannels(c => c.State == ChannelState.Open && IsPublic(c))
                                               .ToList();
        var checkedIds = channels.Select(c => c.ChannelId).ToHashSet();
        foreach (var channelId in _offlineSince.Keys.Where(id => !checkedIds.Contains(id)))
            _offlineSince.TryRemove(channelId, out _);
        foreach (var channelId in _disabledOffline.Keys.Where(id => !checkedIds.Contains(id)))
            _disabledOffline.TryRemove(channelId, out _);

        var now = _timeProvider.GetUtcNow();
        foreach (var channel in channels)
        {
            var channelId = channel.ChannelId;
            if (await probe.IsAliveAsync(channelId, channel.RemoteNodeId, cancellationToken))
            {
                _offlineSince.TryRemove(channelId, out _);
                if (_disabledOffline.TryRemove(channelId, out _))
                {
                    _logger.LogInformation("The link of announced channel {ChannelId} is up again: enabling it",
                                           channelId);
                    await SendChannelUpdateAsync(channelId, reuseUnchanged: true, cancellationToken);
                }

                continue;
            }

            // NL-364: without a start in memory, the peer's persisted last-seen time is where its absence began
            var since = _offlineSince.TryGetValue(channelId, out var stored)
                            ? stored
                            : _offlineSince.GetOrAdd(channelId,
                                                     await GetPersistedOfflineStartAsync(channel.RemoteNodeId, now,
                                                         cancellationToken));
            if (now - since >= _disableAfter && !_disabledOffline.ContainsKey(channelId))
                await DisableOfflineChannelAsync(channelId, probe, since, now - since, cancellationToken);
        }
    }

    /// <summary>
    /// The link-up hook of the probe (NL-364): the channel turned Open or was reestablished on the peer's current
    /// connection, so a channel disabled while its peer was away is enabled again at once (a newer update, to the
    /// peer and the relay). Raised while the channel's lock may be held, so only the offline state is touched here and
    /// the send is scheduled. A channel that was not disabled only has its offline time reset.
    /// </summary>
    private void HandleLinkUp(object? sender, ChannelLinkUpEventArgs args)
    {
        _offlineSince.TryRemove(args.ChannelId, out _);
        if (!_disabledOffline.TryRemove(args.ChannelId, out _))
            return;

        _logger.LogInformation("The link of announced channel {ChannelId} is up again: enabling it", args.ChannelId);
        _ = Task.Run(async () =>
        {
            try
            {
                await SendChannelUpdateAsync(args.ChannelId, reuseUnchanged: true, CancellationToken.None);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Could not re-enable announced channel {ChannelId} after its link came up",
                                   args.ChannelId);
            }
        });
    }

    /// <summary>
    /// The peer's last-seen time from its row (NL-364) as the start of its absence, or <paramref name="now"/> when
    /// there is no row (or no unit of work, or its clock is behind the row). The row's time moves while the peer's
    /// pongs come, so it is where its absence began; a failing read only restarts the count from now, as before.
    /// </summary>
    private async Task<DateTimeOffset> GetPersistedOfflineStartAsync(CompactPubKey peerPubKey, DateTimeOffset now,
                                                                     CancellationToken cancellationToken)
    {
        if (_serviceProvider is null)
            return now;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            if (scope.ServiceProvider.GetService<IUnitOfWork>() is not { } unitOfWork)
                return now;

            var peer = await unitOfWork.PeerDbRepository.GetByNodeIdAsync(peerPubKey).WaitAsync(cancellationToken);
            if (peer is null || peer.LastSeenAt == default)
                return now;

            var lastSeen = new DateTimeOffset(DateTime.SpecifyKind(peer.LastSeenAt, DateTimeKind.Utc), TimeSpan.Zero);
            return lastSeen < now ? lastSeen : now;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogDebug(e, "Could not read the last-seen time of peer {Peer} for its offline start", peerPubKey);
            return now;
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
        _offlineCheckTimer?.Dispose();
        if (_probe is not null)
            _probe.LinkUp -= HandleLinkUp;
        _channelMemoryRepository.OnChannelUpdated -= HandleChannelUpdated;
    }

    /// <summary>Starts an offline check unless the previous one still runs (timer callback).</summary>
    private void StartOfflineCheck()
    {
        if (Interlocked.CompareExchange(ref _offlineCheckRunning, 1, 0) != 0)
            return;

        LastOfflineCheck = Task.Run(async () =>
        {
            try
            {
                await CheckOfflinePeersAsync();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "The offline check of announced channels failed");
            }
            finally
            {
                Volatile.Write(ref _offlineCheckRunning, 0);
            }
        });
    }

    /// <summary>
    /// The disabled update of an announced channel whose peer has been away for <paramref name="offlineFor"/>: to the
    /// relay only (the peer is away, and learns the enabled one when it is back). Under the channel's lock, where the
    /// offline condition is checked again: the peer may have reconnected (and got its update) since the check read the
    /// probe, which restarts the offline time (<paramref name="offlineSince"/> no longer stored), or the link may be up.
    /// </summary>
    private async Task DisableOfflineChannelAsync(ChannelId channelId, IPeerLivenessProbe probe,
                                                  DateTimeOffset offlineSince, TimeSpan offlineFor,
                                                  CancellationToken cancellationToken)
    {
        using var channelLock = await _channelLockProvider.AcquireAsync(channelId, cancellationToken);
        if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel) || channel.State != ChannelState.Open
         || !IsPublic(channel) || !TryGetPolicy(channel, out _, out _))
            return;

        if (!_offlineSince.TryGetValue(channelId, out var storedSince) || storedSince != offlineSince
         || await probe.IsAliveAsync(channelId, channel.RemoteNodeId, cancellationToken))
        {
            _logger.LogDebug("Not disabling announced channel {ChannelId}: its peer reconnected or its link is up",
                             channelId);
            return;
        }

        if (!_disabledOffline.TryAdd(channelId, 0))
            return;

        try
        {
            CreateChannelUpdate(channel, disabled: true);
            _logger.LogInformation(
                "The peer {Peer} of announced channel {ChannelId} has been away for {OfflineFor}: its channel_update "
              + "is now disabled", channel.RemoteNodeId, channelId, offlineFor);
        }
        catch (Exception e)
        {
            _disabledOffline.TryRemove(channelId, out _);
            _logger.LogWarning(e, "Could not make the disabled channel_update of channel {ChannelId}", channelId);
        }
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

        if (channel.State != ChannelState.Open || channel.ShortChannelId == default)
            return;

        var channelId = channel.ChannelId;
        var current = channel.ShortChannelId;
        var hadPrevious = _knownShortChannelIds.TryGetValue(channelId, out var previous);
        _knownShortChannelIds[channelId] = current;
        if (!_sentOnOpen.TryAdd(channelId, 0))
        {
            // A splice lock (or a reorg, NL-350) moved the short channel id of a channel we already sent an update for
            if (hadPrevious && previous != current)
                ScheduleShortChannelIdSwitch(channelId, previous, current);
            return;
        }

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
    /// The channel's short channel id moved while it is open (splicing plan D12, SP2-B-T2: a splice lock; also a reorg
    /// that confirmed the funding elsewhere, NL-350): a new update for the new short channel id goes to the peer once the
    /// caller released the channel's lock (it waits for it, so it follows the lock's own messages and sees the
    /// announcement state the lock reset). It is private (<c>dont_forward</c>) until the splice is announced, which
    /// signs the public one (<see cref="OnChannelAnnounced"/>). Our update for the old short channel id is neither
    /// disabled nor withdrawn: the network forgets that channel 72 blocks after its funding output was spent (BOLT 7),
    /// and payers that still use it are forwarded through the retired short channel id map for as long.
    /// </summary>
    private void ScheduleShortChannelIdSwitch(ChannelId channelId, ShortChannelId previous, ShortChannelId current)
    {
        _logger.LogInformation("The short channel id of channel {ChannelId} moved from {Previous} to {Current}; "
                             + "sending a channel_update for the new one", channelId, previous, current);
        _ = Task.Run(async () =>
        {
            try
            {
                await SendChannelUpdateAsync(channelId);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to send the channel_update of channel {ChannelId} for {ShortChannelId}",
                                 channelId, current);
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
    /// With <paramref name="shortChannelIdOverride"/> (NL-266), that short channel id stands in for the one the
    /// channel's standing update carries (the rest of the policy is the channel's own); the caller checked that it is
    /// one of the channel's names.
    /// </summary>
    private bool TryGetPolicy(ChannelModel channel, out UpdatePolicy policy, out string reason,
                              ShortChannelId? shortChannelIdOverride = null)
    {
        policy = default;

        ShortChannelId shortChannelId;
        if (shortChannelIdOverride is { } overridden)
        {
            shortChannelId = overridden;
        }
        else if (IsPublic(channel))
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

        var effective = _channelPolicyProvider?.GetEffectivePolicy(channel)
                     ?? ChannelPolicyRules.Resolve(channel, _nodeOptions.Routing, null);
        if (_channelPolicyProvider is { IsLoaded: false })
        {
            // Node:Routing's values are not the channel's policy when it has an override we could not read
            reason = "the channel routing policy overrides could not be loaded";
            return false;
        }

        // BOLT 7: htlc_maximum_msat MUST NOT exceed the capacity, so it can't be raised to the minimum
        if (effective.HtlcMinimumMsat > effective.HtlcMaximumMsat)
        {
            reason = $"htlc_minimum_msat {effective.HtlcMinimumMsat} is above the largest HTLC it can carry "
                   + $"({effective.HtlcMaximumMsat} msat: capacity, the peer's max_htlc_value_in_flight_msat, the "
                   + "channel's or Routing's HtlcMaximumMsat)";
            return false;
        }

        policy = new UpdatePolicy(shortChannelId, effective.HtlcMinimumMsat, effective.HtlcMaximumMsat,
                                  effective.FeeBaseMsat, effective.FeeProportionalMillionths,
                                  effective.CltvExpiryDelta);
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Our (unsigned) update for the channel with its current routing policy (field rules in the class remarks).
    /// </summary>
    private ChannelUpdatePayload BuildUnsignedUpdate(ChannelModel channel, UpdatePolicy policy, bool disabled,
                                                     uint timestamp)
    {
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
                                        policy.CltvExpiryDelta, policy.HtlcMinimumMsat, policy.FeeBaseMsat,
                                        policy.FeeProportionalMillionths, policy.HtlcMaximumMsat);
    }

    /// <summary>
    /// Whether <paramref name="last"/> still says what a new update would (all but timestamp and signature), enabled
    /// or <paramref name="disabled"/>.
    /// </summary>
    private bool IsCurrent(ChannelModel channel, UpdatePolicy policy, ChannelUpdatePayload last, bool disabled)
    {
        var current = BuildUnsignedUpdate(channel, policy, disabled, last.Timestamp);
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
    /// The short channel id, HTLC limits, fee and CLTV delta our update for a channel carries.
    /// </summary>
    private readonly record struct UpdatePolicy(ShortChannelId ShortChannelId, ulong HtlcMinimumMsat,
                                                ulong HtlcMaximumMsat, uint FeeBaseMsat,
                                                uint FeeProportionalMillionths, ushort CltvExpiryDelta);
}