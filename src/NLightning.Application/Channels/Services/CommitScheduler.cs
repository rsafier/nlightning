using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Channels.Services;

using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Interfaces;

/// <summary>
/// Options of the <see cref="CommitScheduler"/>.
/// </summary>
public sealed class CommitSchedulerOptions
{
    /// <summary>
    /// How long to wait after an update before signing, so several updates made together share one
    /// <c>commitment_signed</c> (BOLT2 plan §3.4: ~10 ms). Zero signs on the next turn of the thread pool.
    /// </summary>
    public TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(10);
}

/// <summary>
/// Signs the peer's next commitment after our own updates (BOLT2 plan N6-T2): debounced per channel, only while the
/// channel's link is up (<see cref="IPeerLivenessProbe"/>, checked again under the lock), never while a signed
/// commitment waits for its <c>revoke_and_ack</c> (D7), persisted with its diff before it is enqueued (D3, D4, through
/// <see cref="ChannelStateTransitionService.SignIfPendingAsync"/>).
/// </summary>
/// <remarks>
/// Singleton. A scheduled signature runs on the thread pool without the caller's execution context, so its message
/// goes to the peer's current connection rather than to the connection whose inbound loop asked for it. A failed
/// signature is logged: the changes stay pending and the next update, <c>revoke_and_ack</c> or reestablish signs them.
/// </remarks>
public sealed class CommitScheduler : ICommitScheduler
{
    private readonly IChannelLockProvider _channelLockProvider;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IChannelMessagePublisher _channelMessagePublisher;
    private readonly TimeSpan _debounce;
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();
    private readonly ILogger<CommitScheduler> _logger;
    private readonly IPeerLivenessProbe _peerLivenessProbe;
    private readonly ConcurrentDictionary<ChannelId, byte> _scheduled = new();
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public CommitScheduler(IChannelLockProvider channelLockProvider, IChannelMemoryRepository channelMemoryRepository,
                           IChannelMessagePublisher channelMessagePublisher, ILogger<CommitScheduler> logger,
                           IPeerLivenessProbe peerLivenessProbe, IServiceScopeFactory serviceScopeFactory,
                           IOptions<CommitSchedulerOptions>? options = null)
    {
        _channelLockProvider = channelLockProvider;
        _channelMemoryRepository = channelMemoryRepository;
        _channelMessagePublisher = channelMessagePublisher;
        _logger = logger;
        _peerLivenessProbe = peerLivenessProbe;
        _serviceScopeFactory = serviceScopeFactory;
        _debounce = options?.Value.Debounce ?? new CommitSchedulerOptions().Debounce;
    }

    /// <inheritdoc />
    public void Schedule(ChannelId channelId)
    {
        // One waiting round per channel: requests made during the debounce are covered by it
        if (!_scheduled.TryAdd(channelId, 0))
            return;

        Task round;
        using (ExecutionContext.SuppressFlow())
            round = Task.Run(() => RunScheduledAsync(channelId));

        _inFlight.TryAdd(round, 0);
        _ = round.ContinueWith(t => _inFlight.TryRemove(t, out _), TaskScheduler.Default);
    }

    /// <inheritdoc />
    public async Task<bool> SignNowAsync(ChannelId channelId, CancellationToken cancellationToken = default)
    {
        // Cheap checks without the lock first: most calls find nothing to sign
        if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel)
         || channel.Commitments is not { CanSendCommit: true })
            return false;

        // Ping before commit: don't sign for a peer that is gone (the changes stay pending for the reestablish)
        if (!await IsLinkUpAsync(channel, cancellationToken))
            return false;

        using var scope = _serviceScopeFactory.CreateScope();
        using var channelLock = await _channelLockProvider.AcquireAsync(channelId, cancellationToken);

        // Re-read under the lock: a message may have signed or changed the channel meanwhile, and the connection may
        // have changed (a commitment_signed must not go to a new connection before channel_reestablish)
        if (!_channelMemoryRepository.TryGetChannel(channelId, out channel) || channel.Commitments is null
         || !await IsLinkUpAsync(channel, cancellationToken))
            return false;

        var transitions = scope.ServiceProvider.GetRequiredService<ChannelStateTransitionService>();
        var commitmentSigned = await transitions.SignIfPendingAsync(channel);
        if (commitmentSigned is null)
            return false;

        _channelMessagePublisher.Publish(channel.RemoteNodeId, [commitmentSigned]);
        return true;
    }

    /// <inheritdoc />
    public async Task WhenIdleAsync()
    {
        while (!_inFlight.IsEmpty)
            await Task.WhenAll(_inFlight.Keys.ToArray());
    }

    private async Task<bool> IsLinkUpAsync(ChannelModel channel, CancellationToken cancellationToken)
    {
        if (await _peerLivenessProbe.IsAliveAsync(channel.ChannelId, channel.RemoteNodeId, cancellationToken))
            return true;

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Not signing channel {ChannelId}: peer {Peer} is not connected on the channel's link",
                             channel.ChannelId, channel.RemoteNodeId);
        return false;
    }

    private async Task RunScheduledAsync(ChannelId channelId)
    {
        try
        {
            if (_debounce > TimeSpan.Zero)
                await Task.Delay(_debounce);
        }
        finally
        {
            // From here a new request starts a new round, so an update made while this one signs is not missed
            _scheduled.TryRemove(channelId, out _);
        }

        try
        {
            await SignNowAsync(channelId);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to sign a commitment for channel {ChannelId}; its changes stay pending",
                             channelId);
        }
    }
}