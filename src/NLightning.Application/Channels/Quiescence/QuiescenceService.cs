using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Channels.Quiescence;

using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Quiescence;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Node;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Interfaces;
using Payments.Switch;
using Reestablish;

/// <summary>
/// The per-channel quiescence state (BOLT 2 "Channel Quiescence"; splicing plan §3.2, Q1-T3): our requests, the
/// <c>stfu</c> exchange, who is the initiator, the end of quiescence and its disconnection.
/// </summary>
/// <remarks>
/// <para>Singleton, memory only (plan D1). Every change of a channel's entry happens under that channel's lock
/// (<see cref="RequestAsync"/> takes it; <see cref="OnStfuReceived"/>, <see cref="TryReleaseStfu"/> and
/// <see cref="Terminate"/> are called under it), except the clearing on disconnection, which needs no lock: a
/// reconnection starts from <see cref="QuiescenceState.None"/>.</para>
/// <para>An entry belongs to the peer's connection it started on (the <see cref="PeerModel"/> instance
/// <see cref="IPeerManager.GetPeer"/> returned then, as <c>ConnectedPeerLivenessProbe</c> pins links): when the
/// connection closes (its <c>OnDisconnect</c>) or another one replaces it, the entry is gone (Q-R-04), even if
/// <see cref="OnPeerDisconnected"/> is never called. Without a peer manager (in-process harnesses) only
/// <see cref="OnPeerDisconnected"/> ends it.</para>
/// <para>Our <c>stfu</c> goes out only when none of our updates is pending for either side (Q-S-02, checked by
/// <see cref="HasPendingLocalUpdates"/>): an owed one is released after each commitment transition through
/// <see cref="IStfuReleaseScheduler"/>, behind the transition's own messages. While a channel quiesces or is
/// quiescent, <c>ChannelOperationsService</c> refuses our updates with <see cref="ChannelQuiescentException"/>; when
/// the quiescence ends (<see cref="Terminate"/>), the channel's pending HTLC events are replayed into the switch
/// (<see cref="LinkUpEventReplayer"/>), so a fulfill or fail refused meanwhile is sent then (never lost), and the
/// commit scheduler signs whatever is pending.</para>
/// <para>The BOLT 2 rules are applied by private helpers here (<see cref="HasPendingLocalUpdates"/>,
/// <see cref="ResolveInitiator"/>) until lane Q-A's Domain <c>QuiescenceRules</c> replaces them.</para>
/// </remarks>
public sealed class QuiescenceService : IQuiescenceService, IStfuReleaseScheduler, IDisposable
{
    private readonly IChannelLockProvider _channelLockProvider;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ConcurrentDictionary<ChannelId, Entry> _entries = new();
    private readonly ILogger<QuiescenceService> _logger;
    private readonly IMessageFactory _messageFactory;
    private readonly NodeOptions _nodeOptions;
    private readonly ConcurrentDictionary<Task, byte> _running = new();
    private readonly ConcurrentDictionary<ChannelId, byte> _scheduledReleases = new();
    private readonly IServiceProvider _serviceProvider;
    private readonly ConditionalWeakTable<IPeerService, object> _subscribedConnections = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly TimeProvider _timeProvider;
    private readonly Lock _sync = new();

    private LinkUpEventReplayer? _ownReplayer;

    public QuiescenceService(IChannelLockProvider channelLockProvider, IChannelMemoryRepository channelMemoryRepository,
                             ILogger<QuiescenceService> logger, IMessageFactory messageFactory,
                             IServiceProvider serviceProvider, IOptions<NodeOptions>? nodeOptions = null,
                             TimeProvider? timeProvider = null)
    {
        _channelLockProvider = channelLockProvider;
        _channelMemoryRepository = channelMemoryRepository;
        _logger = logger;
        _messageFactory = messageFactory;
        _serviceProvider = serviceProvider;
        _nodeOptions = nodeOptions?.Value ?? new NodeOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    #region IQuiescenceService

    /// <inheritdoc />
    public QuiescenceState GetState(ChannelId channelId) =>
        _entries.TryGetValue(channelId, out var entry) && IsCurrent(entry) ? entry.State : QuiescenceState.None;

    /// <inheritdoc />
    public async Task<QuiescenceInitiator> RequestAsync(ChannelId channelId, QuiescencePurpose purpose,
                                                        CancellationToken cancellationToken = default)
    {
        Entry entry;
        using (await _channelLockProvider.AcquireAsync(channelId, cancellationToken))
        {
            if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel))
                throw new KeyNotFoundException($"Channel {channelId} is not loaded");

            if (channel.State != ChannelState.Open)
                throw new InvalidOperationException(
                    $"Channel {channelId} is {Enum.GetName(channel.State)}: only an Open channel can quiesce");

            if (!IsNegotiatedWith(channel.RemoteNodeId))
                throw new InvalidOperationException(
                    $"[Q-S-01] option_quiesce is not negotiated with the peer of channel {channelId}");

            if (TryGetCurrent(channelId, out var existing))
                throw new InvalidOperationException(
                    $"Channel {channelId} already quiesces ({Describe(existing.State)})");

            entry = CreateEntry(channel, new QuiescenceState { PendingRequest = purpose }, withWaiter: true);
            _entries[channelId] = entry;
            _logger.LogInformation("Quiescence of channel {ChannelId} requested for {Purpose}", channelId, purpose);

            // Sent at once when nothing of ours is pending, else released after the transition that drains it
            if (TryReleaseStfu(channel) is { } stfu)
                GetPublisher()?.Publish(channel.RemoteNodeId, [stfu]);
        }

        // Our unsigned updates must be signed (and revoked) before our stfu may go out
        _serviceProvider.GetService<ICommitScheduler>()?.Schedule(channelId);

        await using var registration = cancellationToken.Register(() => TrackBackground(
                                                                      CancelRequestAsync(channelId, entry)));
        return await entry.Waiter!.Task.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public StfuMessage? OnStfuReceived(ChannelModel channel, StfuPayload stfu, FeatureSet negotiatedFeatures)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(stfu);
        ArgumentNullException.ThrowIfNull(negotiatedFeatures);
        var channelId = channel.ChannelId;

        if (!negotiatedFeatures.IsFeatureSet(Feature.OptionQuiesce))
            throw new ChannelWarningException($"[Q-S-01] stfu on channel {channelId} without option_quiesce",
                                              channelId, "stfu without option_quiesce negotiated")
            {
                CloseConnection = true
            };

        if (channel.State != ChannelState.Open)
            throw new ChannelWarningException(
                $"stfu on channel {channelId} in state {Enum.GetName(channel.State)}", channelId,
                "stfu on a channel that is not open")
            {
                CloseConnection = true
            };

        lock (_sync)
        {
            if (!TryGetCurrent(channelId, out var entry))
            {
                entry = CreateEntry(channel, QuiescenceState.None, withWaiter: false);
                _entries[channelId] = entry;
            }

            if (entry.State.StfuReceived)
                throw new ChannelWarningException($"[Q-S-03] second stfu on channel {channelId}", channelId,
                                                  "stfu sent twice")
                {
                    CloseConnection = true
                };

            entry.Negotiated = true;
            entry.StartedAt ??= _timeProvider.GetUtcNow();
            entry.State = entry.State with { ReceivedStfuInitiator = stfu.Initiator };
            _logger.LogInformation("Peer {Peer} sent stfu (initiator {Initiator}) on channel {ChannelId}",
                                   channel.RemoteNodeId, stfu.Initiator ? 1 : 0, channelId);

            if (entry.State.StfuSent)
            {
                // Q-R-01: we had sent ours, so the channel is quiescent now
                BecomeQuiescent(channel, entry);
                return null;
            }
        }

        // Q-R-02: reply once our own changes are committed and revoked both ways
        return TryReleaseStfu(channel);
    }

    /// <inheritdoc />
    public StfuMessage? TryReleaseStfu(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var channelId = channel.ChannelId;
        lock (_sync)
        {
            if (!TryGetCurrent(channelId, out var entry) || entry.State.StfuSent)
                return null;

            if (entry.State is { PendingRequest: null, StfuReceived: false })
                return null;

            if (!CanSendStfu(channel, entry))
                return null;

            // Q-S-03: a reply sets initiator = 0, our own request 1
            var initiator = !entry.State.StfuReceived;
            entry.StartedAt ??= _timeProvider.GetUtcNow();
            entry.State = entry.State with { SentStfuInitiator = initiator };
            _logger.LogInformation("Sending stfu (initiator {Initiator}) on channel {ChannelId}", initiator ? 1 : 0,
                                   channelId);

            if (entry.State.StfuReceived)
                BecomeQuiescent(channel, entry);

            return _messageFactory.CreateStfuMessage(channelId, initiator);
        }
    }

    /// <inheritdoc />
    public void Terminate(ChannelId channelId, QuiescenceEndReason reason)
    {
        Entry? entry;
        lock (_sync)
        {
            if (!_entries.TryRemove(channelId, out entry))
                return;
        }

        EndEntry(entry, reason);
        if (reason != QuiescenceEndReason.Disconnected)
            ScheduleResume(channelId);
    }

    /// <inheritdoc />
    public void OnPeerDisconnected(CompactPubKey peerPubKey)
    {
        List<Entry> ended = [];
        lock (_sync)
        {
            foreach (var pair in _entries)
                if (pair.Value.Peer == peerPubKey && _entries.TryRemove(pair))
                    ended.Add(pair.Value);
        }

        foreach (var entry in ended)
            EndEntry(entry, QuiescenceEndReason.Disconnected);
    }

    #endregion

    #region Release and resume

    /// <inheritdoc />
    public void ScheduleRelease(ChannelId channelId)
    {
        if (!_entries.TryGetValue(channelId, out var entry) || entry.State.StfuSent
         || entry.State is { PendingRequest: null, StfuReceived: false })
            return;

        // One waiting round per channel: a transition made before it runs is covered by it
        if (!_scheduledReleases.TryAdd(channelId, 0))
            return;

        Task round;
        using (ExecutionContext.SuppressFlow())
            round = Task.Run(() => ReleaseRoundAsync(channelId));
        TrackBackground(round);
    }

    /// <summary>
    /// Completes when no release round, cancellation or replay started by this service is running (tests).
    /// </summary>
    public async Task WhenIdleAsync()
    {
        while (!_running.IsEmpty)
            await Task.WhenAll(_running.Keys.ToArray());

        if (_ownReplayer is { } replayer)
            await replayer.WhenIdleAsync();
        if (_serviceProvider.GetService<LinkUpEventReplayer>() is { } registered)
            await registered.WhenIdleAsync();
    }

    private async Task ReleaseRoundAsync(ChannelId channelId)
    {
        _scheduledReleases.TryRemove(channelId, out _);
        try
        {
            using var channelLock = await _channelLockProvider.AcquireAsync(channelId, _stopping.Token);
            if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel))
                return;

            // The link check of every send: the peer is on the connection the channel was reestablished on
            var probe = _serviceProvider.GetService<IPeerLivenessProbe>();
            if (probe is not null && !await probe.IsAliveAsync(channelId, channel.RemoteNodeId, _stopping.Token))
                return;

            if (TryReleaseStfu(channel) is { } stfu)
                GetPublisher()?.Publish(channel.RemoteNodeId, [stfu]);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Shutting down
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to release the stfu of channel {ChannelId}", channelId);
        }
    }

    /// <summary>
    /// After the end of a quiescence: the pending HTLC events of the channel go to the switch again (a fulfill or fail
    /// refused while quiescing is sent now) and the commit scheduler signs what is pending. Both run in the background.
    /// </summary>
    private void ScheduleResume(ChannelId channelId)
    {
        try
        {
            GetReplayer().Schedule(channelId);
            _serviceProvider.GetService<ICommitScheduler>()?.Schedule(channelId);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to resume channel {ChannelId} after its quiescence", channelId);
        }
    }

    private LinkUpEventReplayer GetReplayer()
    {
        if (_serviceProvider.GetService<LinkUpEventReplayer>() is { } registered)
            return registered;

        lock (_sync)
            return _ownReplayer ??= new LinkUpEventReplayer(
                       _channelLockProvider, _channelMemoryRepository,
                       _serviceProvider.GetService<ILogger<LinkUpEventReplayer>>()
                    ?? NullLogger<LinkUpEventReplayer>.Instance, _serviceProvider);
    }

    private async Task CancelRequestAsync(ChannelId channelId, Entry entry)
    {
        try
        {
            using var channelLock = await _channelLockProvider.AcquireAsync(channelId, _stopping.Token);
            lock (_sync)
            {
                // A stfu already sent can't be withdrawn: then only the dependent protocol or a disconnection ends it
                if (!_entries.TryGetValue(channelId, out var current) || current != entry || entry.State.StfuSent)
                    return;

                if (entry.State.StfuReceived)
                {
                    // The peer asked too: we still owe it our reply, so only our request is withdrawn
                    entry.State = entry.State with { PendingRequest = null };
                    entry.Waiter?.TrySetCanceled();
                    return;
                }

                _entries.TryRemove(channelId, out _);
            }

            entry.Waiter?.TrySetCanceled();
            EndEntry(entry, QuiescenceEndReason.RequestCancelled);
            ScheduleResume(channelId);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Shutting down
        }
    }

    #endregion

    #region Monitor view

    /// <summary>
    /// The channels that are quiescing or quiescent, for <see cref="QuiescenceTimeoutMonitor"/>: channel, peer, when
    /// the quiescence started and whether it is quiescent. Stale entries (a replaced connection) are left out.
    /// </summary>
    public IReadOnlyList<QuiescenceSnapshot> GetActive() =>
        _entries.Where(pair => IsCurrent(pair.Value))
                .Select(pair => new QuiescenceSnapshot(pair.Key, pair.Value.Peer, pair.Value.State,
                                                       pair.Value.StartedAt ?? pair.Value.CreatedAt))
                .ToList();

    #endregion

    /// <summary>Stops the background rounds (container disposal).</summary>
    public void Dispose() => _stopping.Cancel();

    #region Rules

    /// <summary>
    /// Q-S-02: one of <b>our</b> HTLC additions, removals or fee updates is pending for either side: our add not yet in
    /// both commitments with both revoked (states 10-13), our removal of the peer's HTLC not final (35-38), or our fee
    /// update not final. The peer's updates never block our <c>stfu</c>.
    /// </summary>
    internal static bool HasPendingLocalUpdates(ChannelCommitments commitments)
    {
        ArgumentNullException.ThrowIfNull(commitments);
        return commitments.Htlcs.Values.Any(h => h.State is >= HtlcState.SentAddHtlc and <= HtlcState.RcvdAddAckCommit
                                                         or >= HtlcState.SentRemoveHtlc
                                                            and <= HtlcState.RcvdRemoveAckCommit)
            || commitments.FeeUpdates.Any(f => !f.IsFinal && f.Owner == HtlcDirection.Outgoing);
    }

    /// <summary>
    /// Who is the initiator once both <c>stfu</c> are exchanged: the side that sent <c>initiator</c> = 1 first; both
    /// 1 (simultaneous) → the channel funder, the sender of <c>open_channel</c> (Q-R-05).
    /// </summary>
    internal static QuiescenceInitiator ResolveInitiator(bool sentInitiator, bool receivedInitiator, bool weAreFunder)
    {
        if (sentInitiator && receivedInitiator)
            return weAreFunder ? QuiescenceInitiator.Local : QuiescenceInitiator.Remote;

        return sentInitiator ? QuiescenceInitiator.Local : QuiescenceInitiator.Remote;
    }

    private bool CanSendStfu(ChannelModel channel, Entry entry)
    {
        if (!entry.Negotiated || channel.State != ChannelState.Open || channel.Commitments is not { } commitments)
            return false;

        // Reestablished on the current connection (nothing goes out before channel_reestablish, B2-RE-07)
        if (_serviceProvider.GetService<ReestablishTracker>() is { } tracker
         && !tracker.IsReestablished(channel.ChannelId))
            return false;

        return !HasPendingLocalUpdates(commitments);
    }

    #endregion

    #region Entries

    private void BecomeQuiescent(ChannelModel channel, Entry entry)
    {
        var initiator = ResolveInitiator(entry.State.SentStfuInitiator!.Value, entry.State.ReceivedStfuInitiator!.Value,
                                         channel.IsInitiator);
        entry.State = entry.State with { Initiator = initiator, QuiescentSince = _timeProvider.GetUtcNow() };
        _logger.LogInformation("Channel {ChannelId} is quiescent (initiator: {Initiator})", channel.ChannelId,
                               initiator);
        entry.Waiter?.TrySetResult(initiator);
    }

    private Entry CreateEntry(ChannelModel channel, QuiescenceState state, bool withWaiter)
    {
        var peer = GetPeerManager()?.GetPeer(channel.RemoteNodeId);
        var entry = new Entry(channel.RemoteNodeId, peer, _timeProvider.GetUtcNow())
        {
            State = state,
            Negotiated = IsNegotiatedWith(channel.RemoteNodeId),
            StartedAt = state.PendingRequest.HasValue ? _timeProvider.GetUtcNow() : null,
            Waiter = withWaiter
                         ? new TaskCompletionSource<QuiescenceInitiator>(
                             TaskCreationOptions.RunContinuationsAsynchronously)
                         : null
        };

        if (peer is not null && peer.TryGetPeerService(out var peerService))
            SubscribeDisconnect(peer, peerService);

        _serviceProvider.GetService<QuiescenceTimeoutMonitor>()?.EnsureStarted();
        return entry;
    }

    /// <summary>Clears the entries of a connection when it closes (Q-R-04), once per connection.</summary>
    private void SubscribeDisconnect(PeerModel connection, IPeerService peerService)
    {
        lock (_sync)
        {
            if (_subscribedConnections.TryGetValue(peerService, out _))
                return;
            _subscribedConnections.Add(peerService, connection);
        }

        peerService.OnDisconnect += (_, _) => ClearConnection(connection);
    }

    private void ClearConnection(PeerModel connection)
    {
        List<Entry> ended = [];
        lock (_sync)
        {
            foreach (var pair in _entries)
                if (ReferenceEquals(pair.Value.Connection, connection) && _entries.TryRemove(pair))
                    ended.Add(pair.Value);
        }

        foreach (var entry in ended)
            EndEntry(entry, QuiescenceEndReason.Disconnected);
    }

    private void EndEntry(Entry entry, QuiescenceEndReason reason)
    {
        _logger.LogInformation("Quiescence of the channel with {Peer} ended: {Reason} ({State})", entry.Peer, reason,
                               Describe(entry.State));
        entry.Waiter?.TrySetException(new InvalidOperationException(
                                          $"The quiescence ended ({reason}) before the channel became quiescent"));
    }

    /// <summary>The live entry of the channel; a stale one (its connection was replaced) is dropped.</summary>
    private bool TryGetCurrent(ChannelId channelId, out Entry entry)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(channelId, out entry!))
                return false;
            if (IsCurrent(entry))
                return true;

            _entries.TryRemove(channelId, out _);
        }

        EndEntry(entry, QuiescenceEndReason.Disconnected);
        entry = null!;
        return false;
    }

    private bool IsCurrent(Entry entry)
    {
        if (entry.Connection is null)
            return true;

        var peerManager = GetPeerManager();
        return peerManager is null || ReferenceEquals(peerManager.GetPeer(entry.Peer), entry.Connection);
    }

    /// <summary>
    /// Q-S-01: <c>option_quiesce</c> negotiated with the peer (its current connection's features); without a peer
    /// manager (in-process harnesses) whether we advertise it.
    /// </summary>
    private bool IsNegotiatedWith(CompactPubKey peerPubKey)
    {
        var peerManager = GetPeerManager();
        if (peerManager is null)
            return _nodeOptions.Features.OptionQuiesce != FeatureSupport.No;

        var peer = peerManager.GetPeer(peerPubKey);
        return peer is not null && peer.TryGetPeerService(out var peerService)
                                && peerService.Features is { OptionQuiesce: not FeatureSupport.No };
    }

    private IPeerManager? GetPeerManager() => _serviceProvider.GetService<IPeerManager>();

    private IChannelMessagePublisher? GetPublisher() => _serviceProvider.GetService<IChannelMessagePublisher>();

    private void TrackBackground(Task task)
    {
        _running[task] = 0;
        task.ContinueWith(t => _running.TryRemove(t, out _), CancellationToken.None,
                          TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        if (task.IsCompleted)
            _running.TryRemove(task, out _);
    }

    private static string Describe(QuiescenceState state) =>
        state.IsQuiescent
            ? $"quiescent, initiator {state.Initiator}"
            : $"quiescing: request {state.PendingRequest?.ToString() ?? "none"}, stfu sent {state.StfuSent}, "
            + $"received {state.StfuReceived}";

    /// <summary>One channel's quiescence; mutated under the channel's lock and <see cref="_sync"/>.</summary>
    private sealed class Entry(CompactPubKey peer, PeerModel? connection, DateTimeOffset createdAt)
    {
        public CompactPubKey Peer { get; } = peer;
        public PeerModel? Connection { get; } = connection;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public QuiescenceState State { get; set; } = QuiescenceState.None;
        public bool Negotiated { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public TaskCompletionSource<QuiescenceInitiator>? Waiter { get; init; }
    }

    #endregion
}

/// <summary>A channel's quiescence as <see cref="QuiescenceService.GetActive"/> lists it.</summary>
/// <param name="ChannelId">The channel.</param>
/// <param name="PeerPubKey">Its peer.</param>
/// <param name="State">Its quiescence state.</param>
/// <param name="Since">When it started quiescing (our request, or the first <c>stfu</c>).</param>
public sealed record QuiescenceSnapshot(ChannelId ChannelId, CompactPubKey PeerPubKey, QuiescenceState State,
                                        DateTimeOffset Since);