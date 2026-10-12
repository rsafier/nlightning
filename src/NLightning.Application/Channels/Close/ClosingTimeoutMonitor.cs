using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Channels.Close;

using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Interfaces;
using Safety.Interfaces;

/// <summary>
/// The two "reasonable amount of time" rules of the legacy <c>closing_signed</c> negotiation (BOLT 2, NL-284): the
/// sender of a <c>closing_signed</c> MUST fail the channel when no <c>closing_signed</c> answers it in time
/// (B2-CLS-03), and a node that saw no overlap between the peer's <c>fee_range</c> and its own MUST fail the channel
/// when no satisfying <c>fee_range</c> follows in time (B2-CLS-R04). Both fail through
/// <see cref="IChannelFailureService"/> (Failed, our latest commitment broadcast).
/// </summary>
/// <remarks>
/// <para>Singleton. The deadlines live in <see cref="ClosingNegotiationRegistry.Entry"/> and are set and cleared by
/// <see cref="ChannelCloseCoordinator"/> under the channel's lock; this class only runs one timer per channel. The reply
/// deadline is dropped when the connection changes (BOLT 2 restarts the negotiation on reconnection, B2-RE-29, and our
/// <c>closing_signed</c> on the new connection sets it again), and it only counts while the peer is on the connection the
/// channel's link is pinned to (<see cref="IPeerLivenessProbe"/>), so a peer that is away is not failed for it; the
/// <c>fee_range</c> deadline survives reconnections (a peer that keeps sending a range we can't accept must not escape
/// it by reconnecting). Both are memory only: a restart restarts the negotiation.</para>
/// <para>When a timer fires, the failure runs only if, under the channel's lock, the channel is still
/// <see cref="ChannelState.Negotiating"/> and a deadline is still past
/// (<see cref="ChannelFailureRequest.StillApplies"/>), so a reply that arrives while the failure waits for the lock
/// wins.</para>
/// </remarks>
public sealed class ClosingTimeoutMonitor : IDisposable
{
    /// <summary>The <c>error</c> text for B2-CLS-03.</summary>
    public const string NoReplyPeerMessage = "no closing_signed reply in time";

    /// <summary>The <c>error</c> text for B2-CLS-R04.</summary>
    public const string NoFeeRangePeerMessage = "no satisfying fee_range in time";

    private readonly ChannelCloseOptions _options;
    private readonly ClosingNegotiationRegistry _registry;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ClosingTimeoutMonitor> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<ChannelId, ITimer> _timers = new();
    private readonly ConcurrentDictionary<Task, byte> _running = new();
    private int _disposed;

    public ClosingTimeoutMonitor(ClosingNegotiationRegistry registry, IServiceProvider serviceProvider,
                                 IOptions<ChannelCloseOptions> options, ILogger<ClosingTimeoutMonitor> logger,
                                 TimeProvider? timeProvider = null)
    {
        _registry = registry;
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The clock the deadlines are measured with.</summary>
    public DateTimeOffset Now => _timeProvider.GetUtcNow();

    /// <summary>
    /// We sent a <c>closing_signed</c> that needs an answer: the peer has
    /// <see cref="ChannelCloseOptions.ClosingSignedReplyTimeout"/> to send one (B2-CLS-03). Call under the channel's
    /// lock.
    /// </summary>
    public void ArmReply(ChannelId channelId)
    {
        if (_options.ClosingSignedReplyTimeout <= TimeSpan.Zero)
            return;

        _registry.Get(channelId).ReplyDueAt = Now + _options.ClosingSignedReplyTimeout;
        Schedule(channelId);
    }

    /// <summary>
    /// The peer's <c>fee_range</c> did not overlap ours: it has <see cref="ChannelCloseOptions.FeeRangeTimeout"/>, from
    /// the first such range, to send a satisfying one (B2-CLS-R04). Call under the channel's lock.
    /// </summary>
    public void ArmFeeRange(ChannelId channelId)
    {
        if (_options.FeeRangeTimeout <= TimeSpan.Zero)
            return;

        var entry = _registry.Get(channelId);
        entry.FeeRangeDueAt ??= Now + _options.FeeRangeTimeout;
        Schedule(channelId);
    }

    /// <summary>
    /// Fails <paramref name="channelId"/> when one of its deadlines is past (what a timer does); null when nothing is
    /// due or the channel is not loaded.
    /// </summary>
    public async Task<ChannelFailureOutcome?> CheckAsync(ChannelId channelId,
                                                        CancellationToken cancellationToken = default)
    {
        if (!_registry.TryGet(channelId, out var entry) || entry is null)
            return null;

        // B2-CLS-03 counts on the connection our closing_signed went out on: a peer that dropped is not failed for
        // it (the next connection restarts the negotiation and clears the deadline)
        var replyApplies = entry.ReplyDueAt is not { } replyDue || replyDue > Now
                        || await IsLinkUpAsync(channelId, cancellationToken);
        if (entry.GetExpired(Now, replyApplies) is not { } expired)
        {
            // A deadline moved later since the timer was set, or only the reply deadline of a dropped link is past
            Schedule(channelId, replyApplies);
            return null;
        }

        var failureService = _serviceProvider.GetService<IChannelFailureService>();
        if (failureService is null)
        {
            _logger.LogError("Closing negotiation of channel {ChannelId} timed out ({RequirementId}), but no "
                           + "fail-the-channel service is registered", channelId, expired.RequirementId);
            return null;
        }

        var request = new ChannelFailureRequest(expired.Reason, expired.PeerMessage, true, expired.RequirementId)
        {
            StillApplies = channel => channel.State == ChannelState.Negotiating
                                   && entry.GetExpired(Now, replyApplies) is not null
        };

        try
        {
            var outcome = await failureService.FailChannelAsync(channelId, request, cancellationToken);
            if (outcome.Status != ChannelFailureStatus.NotApplicable)
                _logger.LogWarning("Failed channel {ChannelId} in closing negotiation ({RequirementId}): {Reason}; "
                                 + "{Status}", channelId, expired.RequirementId, expired.Reason, outcome.Status);
            return outcome;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Waits for the checks the timers started (tests).</summary>
    public Task WhenIdleAsync() => Task.WhenAll(_running.Keys);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        foreach (var timer in _timers.Values)
            timer.Dispose();
        _timers.Clear();
    }

    /// <summary>
    /// True when the channel's peer is still on the connection the channel's link is pinned to (the one our
    /// <c>closing_signed</c> went out on); true without a probe or when the channel is not loaded.
    /// </summary>
    private async Task<bool> IsLinkUpAsync(ChannelId channelId, CancellationToken cancellationToken)
    {
        var probe = _serviceProvider.GetService<IPeerLivenessProbe>();
        var channels = _serviceProvider.GetService<IChannelMemoryRepository>();
        if (probe is null || channels is null || !channels.TryGetChannel(channelId, out var channel))
            return true;

        return await probe.IsAliveAsync(channelId, channel.RemoteNodeId, cancellationToken);
    }

    private void Schedule(ChannelId channelId, bool includeReply = true)
    {
        if (Volatile.Read(ref _disposed) != 0 || !_registry.TryGet(channelId, out var entry) || entry is null)
            return;

        if (entry.GetNextDeadline(includeReply) is not { } due)
        {
            if (_timers.TryRemove(channelId, out var stale))
                stale.Dispose();
            return;
        }

        var delay = due - Now;
        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;

        var timer = _timeProvider.CreateTimer(_ => Fire(channelId), null, delay, Timeout.InfiniteTimeSpan);
        _timers.AddOrUpdate(channelId, timer, (_, previous) =>
        {
            previous.Dispose();
            return timer;
        });
    }

    private void Fire(ChannelId channelId)
    {
        var task = Task.Run(async () =>
        {
            try
            {
                await CheckAsync(channelId);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Checking the closing deadlines of channel {ChannelId} failed", channelId);
            }
        });
        _running.TryAdd(task, 0);
        _ = task.ContinueWith(t => _running.TryRemove(t, out _), TaskScheduler.Default);
    }
}