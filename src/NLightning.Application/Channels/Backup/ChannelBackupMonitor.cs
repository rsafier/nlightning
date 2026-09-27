using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Channels.Backup;

using Domain.Channels.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Interfaces;

/// <summary>
/// Keeps the backup file up to date: a write is requested at start, whenever a channel enters or leaves the backup or
/// gets its short channel id (<see cref="IChannelMemoryRepository"/> events; HTLC updates change nothing in a backup
/// and are filtered out), and every <see cref="ChannelBackupOptions.RefreshInterval"/>. Requests coalesce: one write
/// runs at a time, <see cref="ChannelBackupOptions.WriteDelay"/> after the first request, and it writes only when the
/// channels differ from the file (<see cref="IChannelBackupService.WriteFileAsync"/>).
/// </summary>
public sealed class ChannelBackupMonitor : IAsyncDisposable
{
    private readonly IChannelBackupService _backupService;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ChannelBackupOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ChannelBackupMonitor> _logger;
    private readonly ConcurrentDictionary<ChannelId, (bool BackedUp, string ShortChannelId)> _seen = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stopping = new();

    private ITimer? _refreshTimer;
    private Task? _loop;
    private bool _pending;
    private bool _started;
    private bool _stopped;

    public ChannelBackupMonitor(IChannelBackupService backupService, IChannelMemoryRepository channelMemoryRepository,
                                IOptions<ChannelBackupOptions>? options = null, TimeProvider? timeProvider = null,
                                ILogger<ChannelBackupMonitor>? logger = null)
    {
        _backupService = backupService;
        _channelMemoryRepository = channelMemoryRepository;
        _options = options?.Value ?? new ChannelBackupOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ChannelBackupMonitor>.Instance;
    }

    /// <summary>Whether the monitor writes a file at all (enabled and a file configured).</summary>
    public bool IsActive => _options.Enabled && !string.IsNullOrWhiteSpace(_options.FilePath);

    /// <summary>Subscribes to the channel events, starts the refresh timer and requests the first write.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started || _stopped || !IsActive)
                return;

            _started = true;
        }

        _channelMemoryRepository.OnChannelUpdated += HandleChannelUpdated;
        _channelMemoryRepository.OnChannelUpgraded += HandleChannelUpgraded;
        if (_options.RefreshInterval > TimeSpan.Zero)
            _refreshTimer = _timeProvider.CreateTimer(_ => RequestWrite(), null, _options.RefreshInterval,
                                                      _options.RefreshInterval);

        RequestWrite();
    }

    /// <summary>Stops the monitor; a write in progress finishes first.</summary>
    public async Task StopAsync()
    {
        Task? loop;
        lock (_gate)
        {
            if (_stopped)
                return;

            _stopped = true;
            loop = _loop;
        }

        _channelMemoryRepository.OnChannelUpdated -= HandleChannelUpdated;
        _channelMemoryRepository.OnChannelUpgraded -= HandleChannelUpgraded;
        if (_refreshTimer is not null)
            await _refreshTimer.DisposeAsync();

        await _stopping.CancelAsync();
        if (loop is not null)
        {
            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
                // Stopping
            }
        }
    }

    /// <summary>Waits until no write is queued or running (tests).</summary>
    internal async Task WhenIdleAsync()
    {
        while (true)
        {
            Task? loop;
            lock (_gate)
                loop = _loop;

            if (loop is null)
                return;

            await loop;
        }
    }

    /// <summary>Requests a write (coalesced with any request not yet served).</summary>
    public void RequestWrite()
    {
        lock (_gate)
        {
            if (_stopped || !_started)
                return;

            _pending = true;
            if (_loop is not null)
                return;

            _loop = Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            lock (_gate)
            {
                if (!_pending || _stopped)
                {
                    _loop = null;
                    return;
                }
            }

            try
            {
                if (_options.WriteDelay > TimeSpan.Zero)
                    await Task.Delay(_options.WriteDelay, _timeProvider, _stopping.Token);

                // Requests made during the delay are served by this write
                lock (_gate)
                    _pending = false;

                await _backupService.WriteFileAsync(_stopping.Token);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                lock (_gate)
                    _loop = null;

                return;
            }
            catch (Exception e)
            {
                // Retried at the next event or refresh
                _logger.LogError(e, "Writing the channel backup failed");
            }
        }
    }

    private void HandleChannelUpdated(object? sender, ChannelUpdatedEventArgs args)
    {
        try
        {
            var channel = args.Channel;
            var key = (ChannelBackupService.IsBackedUp(channel), channel.ShortChannelId.ToString());
            if (_seen.TryGetValue(channel.ChannelId, out var previous) && previous == key)
                return;

            _seen[channel.ChannelId] = key;
            RequestWrite();
        }
        catch (Exception e)
        {
            // Never fail the caller (it may hold a channel lock)
            _logger.LogError(e, "Channel backup event handling failed");
        }
    }

    private void HandleChannelUpgraded(object? sender, ChannelUpgradedEventArgs args) => RequestWrite();

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stopping.Dispose();
    }
}