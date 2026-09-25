using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Payments.Switch;

using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Persistence.Interfaces;

/// <summary>
/// Hands a channel's pending domain events (<see cref="ChannelDomainEvents.DerivePending(ChannelCommitments, IEnumerable{HtlcRecord})"/>
/// over its snapshot and its unpruned settled rows) to the <see cref="IHtlcSwitch"/> when the channel's link comes up,
/// so a resolution the switch could not send while the peer was away (the channel operation was refused, nothing was
/// persisted) is sent as soon as it can be.
/// </summary>
/// <remarks>
/// <para>Triggered by <see cref="LinkUpReplayingPeerLivenessProbe.MarkLinkUp"/> (registered by
/// <see cref="HtlcSwitchServiceCollectionExtensions.AddHtlcSwitchServices"/>): today when a channel turns Open, and with
/// BOLT2 N7 after every <c>channel_reestablish</c>. The startup replay of <c>ChannelManager</c> runs while no link is up,
/// so without this an HTLC refused because its peer was away would wait for the next restart.</para>
/// <para>Each replay runs on the thread pool without the caller's execution context (the caller holds the channel's
/// lock): the snapshot and the settled rows are read under the channel's lock, which is released before the events are
/// handed to the switch (never two locks). The switch is idempotent, so a replay that overlaps another one (or N7's own)
/// is harmless. A failure is logged; the events stay derivable.</para>
/// </remarks>
public sealed class LinkUpEventReplayer
{
    private readonly IChannelLockProvider _channelLockProvider;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ILogger<LinkUpEventReplayer> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<Task, byte> _running = new();

    public LinkUpEventReplayer(IChannelLockProvider channelLockProvider,
                               IChannelMemoryRepository channelMemoryRepository, ILogger<LinkUpEventReplayer> logger,
                               IServiceProvider serviceProvider)
    {
        _channelLockProvider = channelLockProvider;
        _channelMemoryRepository = channelMemoryRepository;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Starts the replay of <paramref name="channelId"/>'s pending events in the background; returns at once (safe to
    /// call under the channel's lock).
    /// </summary>
    public void Schedule(ChannelId channelId)
    {
        Task replay;
        using (ExecutionContext.SuppressFlow())
            replay = Task.Run(() => ReplayAsync(channelId, CancellationToken.None));

        _running[replay] = 0;
        replay.ContinueWith(t => _running.TryRemove(t, out _), CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        if (replay.IsCompleted)
            _running.TryRemove(replay, out _);
    }

    /// <summary>
    /// Replays the pending events of <paramref name="channelId"/> into the switch now. Never throws (except on
    /// cancellation); a failure is logged.
    /// </summary>
    public async Task ReplayAsync(ChannelId channelId, CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<IChannelDomainEvent> pending;
            using (await _channelLockProvider.AcquireAsync(channelId, cancellationToken))
            {
                if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel)
                 || channel.Commitments is not { } commitments)
                    return;

                using var scope = _serviceProvider.CreateScope();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var persisted = await unitOfWork.ChannelStateDbRepository.LoadAsync(channelId, commitments.Params);
                pending = ChannelDomainEvents.DerivePending(commitments, persisted?.SettledHtlcs);
            }

            if (pending.Count == 0)
                return;

            _logger.LogInformation("Link of channel {ChannelId} is up: replaying {Count} pending HTLC event(s)",
                                   channelId, pending.Count);

            // Resolved here: the switch depends on the liveness probe that calls this
            var htlcSwitch = _serviceProvider.GetRequiredService<IHtlcSwitch>();
            foreach (var channelEvent in pending)
                await htlcSwitch.HandleAsync(channelEvent, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Replaying the pending HTLC events of channel {ChannelId} failed", channelId);
        }
    }

    /// <summary>Completes when no scheduled replay is running (tests, shutdown).</summary>
    public async Task WhenIdleAsync()
    {
        while (_running.Keys.ToArray() is { Length: > 0 } running)
            await Task.WhenAll(running);
    }
}