using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Channels.Quiescence;

using Domain.Channels.Interfaces;
using Domain.Channels.Quiescence;
using Domain.Node.Options;

/// <summary>
/// Closes the connection of a channel that stays quiescing or quiescent too long (BOLT 2 Q-R-03, splicing plan Q1-T5):
/// with HTLCs pending after <see cref="QuiescenceOptions.Timeout"/> (the spec's 60 s), without after
/// <see cref="QuiescenceOptions.IdleTimeout"/> (a MAY of ours, so a stuck peer cannot freeze the channel).
/// </summary>
/// <remarks>
/// <para>Singleton. <see cref="QuiescenceService"/> starts it with the first quiescence (<see cref="EnsureStarted"/>),
/// so the host starts nothing. A timer on the injected <see cref="TimeProvider"/> runs <see cref="CheckAsync"/> every
/// tenth of the shorter limit (1 to 5 s). Both limits count from the start of the quiescence
/// (<see cref="QuiescenceSnapshot.Since"/>: our request, or the first <c>stfu</c>).</para>
/// <para>An expired channel is marked timed out under its lock (<see cref="IQuiescenceService.Terminate"/> with
/// <see cref="QuiescenceEndReason.Timeout"/>, re-checked there), then its connection is closed with a
/// <c>warning</c> through <see cref="IQuiescencePeerDisconnector"/> after the lock. The mark keeps our updates blocked
/// and resumes nothing: the peer considers the channel quiescent until the disconnection, which alone ends it
/// (<see cref="QuiescenceService.Terminate"/>). While the connection stays open the close is tried again every
/// <see cref="QuiescenceOptions.Timeout"/>.</para>
/// </remarks>
public sealed class QuiescenceTimeoutMonitor : IDisposable
{
    private readonly IChannelLockProvider _channelLockProvider;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IQuiescencePeerDisconnector _disconnector;
    private readonly ILogger<QuiescenceTimeoutMonitor> _logger;
    private readonly QuiescenceOptions _options;
    private readonly QuiescenceService _quiescenceService;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _sync = new();

    private ITimer? _timer;
    private int _checking;
    private bool _disposed;

    public QuiescenceTimeoutMonitor(IChannelLockProvider channelLockProvider,
                                    IChannelMemoryRepository channelMemoryRepository,
                                    IQuiescencePeerDisconnector disconnector, ILogger<QuiescenceTimeoutMonitor> logger,
                                    QuiescenceService quiescenceService, IOptions<NodeOptions>? nodeOptions = null,
                                    TimeProvider? timeProvider = null)
    {
        _channelLockProvider = channelLockProvider;
        _channelMemoryRepository = channelMemoryRepository;
        _disconnector = disconnector;
        _logger = logger;
        _quiescenceService = quiescenceService;
        _options = nodeOptions?.Value.Quiescence ?? new QuiescenceOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>How often the timer checks.</summary>
    public TimeSpan CheckInterval
    {
        get
        {
            var shorter = _options.Timeout < _options.IdleTimeout ? _options.Timeout : _options.IdleTimeout;
            var interval = shorter / 10;
            if (interval < TimeSpan.FromSeconds(1))
                return TimeSpan.FromSeconds(1);
            return interval > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : interval;
        }
    }

    /// <summary>Starts the timer if it is not running; safe to call under a channel lock.</summary>
    public void EnsureStarted()
    {
        lock (_sync)
        {
            if (_timer is not null || _disposed)
                return;

            var interval = CheckInterval;
            _timer = _timeProvider.CreateTimer(_ => OnTimer(), null, interval, interval);
        }
    }

    /// <summary>
    /// One check: ends every quiescence past its limit and closes its connection.
    /// </summary>
    /// <returns>The channels whose quiescence was ended.</returns>
    public async Task<IReadOnlyList<QuiescenceSnapshot>> CheckAsync(CancellationToken cancellationToken = default)
    {
        var expired = new List<QuiescenceSnapshot>();
        foreach (var snapshot in _quiescenceService.GetActive())
        {
            if (GetReason(snapshot) is not { } reason)
                continue;

            using (await _channelLockProvider.AcquireAsync(snapshot.ChannelId, cancellationToken))
            {
                // Re-checked under the lock: the quiescence may have ended (or a new one started) meanwhile
                var current = _quiescenceService.GetActive().FirstOrDefault(s => s.ChannelId == snapshot.ChannelId);
                if (current is null || current.Since != snapshot.Since || GetReason(current) is null)
                    continue;

                // The entry stays (still blocking our updates) until the connection is closed: the peer considers the
                // channel quiescent until then (Q-S-04, Q-R-04)
                _quiescenceService.Terminate(snapshot.ChannelId, QuiescenceEndReason.Timeout);
            }

            _logger.LogWarning("Quiescence of channel {ChannelId} timed out ({Reason}); closing the connection to {Peer}",
                               snapshot.ChannelId, reason, snapshot.PeerPubKey);
            _disconnector.Disconnect(snapshot.PeerPubKey, snapshot.ChannelId, reason);
            expired.Add(snapshot);
        }

        return expired;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>
    /// The reason to close the connection now: the quiescence is over its limit, or it timed out at least
    /// <see cref="QuiescenceOptions.Timeout"/> ago and its connection is still open (the close is tried again). Null
    /// otherwise.
    /// </summary>
    private string? GetReason(QuiescenceSnapshot snapshot)
    {
        if (snapshot.TerminatedAt is not { } terminatedAt)
            return GetLimit(snapshot);

        var since = _timeProvider.GetUtcNow() - terminatedAt;
        return since >= _options.Timeout
                   ? $"channel quiescence timed out {since.TotalSeconds:0} s ago and the connection is still open"
                   : null;
    }

    /// <summary>The reason the quiescence is over its limit, or null while it is not.</summary>
    private string? GetLimit(QuiescenceSnapshot snapshot)
    {
        var elapsed = _timeProvider.GetUtcNow() - snapshot.Since;
        var hasHtlcs = _channelMemoryRepository.TryGetChannel(snapshot.ChannelId, out var channel)
                    && channel.Commitments is { Htlcs.IsEmpty: false };

        if (hasHtlcs && elapsed >= _options.Timeout)
            return $"[Q-R-03] channel quiescent for {elapsed.TotalSeconds:0} s with HTLCs pending";
        if (elapsed >= _options.IdleTimeout)
            return $"channel quiescent for {elapsed.TotalSeconds:0} s without finishing";
        return null;
    }

    private void OnTimer()
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1)
            return;

        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            await CheckAsync();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Quiescence timeout check failed");
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }
}