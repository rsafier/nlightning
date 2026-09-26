using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Channels.Close;

using Domain.Bitcoin.Interfaces;

/// <summary>
/// The fee estimate a closing negotiation starts from, shared by every close of the process (W4-E review F3): one
/// fetch at a time through <see cref="IFeeService.GetFeeRatePerKwAsync"/>, the last good value kept for
/// <see cref="ChannelCloseOptions.FeeEstimateMaxAge"/>, and a failed fetch not retried before
/// <see cref="ChannelCloseOptions.FeeEstimateRetryAfter"/>.
/// </summary>
/// <remarks>
/// <para>Singleton. The coordinator runs under the channel's lock, inside the peer's single inbound loop, so it never
/// waits for a fetch longer than <see cref="ChannelCloseOptions.FeeEstimateWaitUnderLock"/>
/// (<see cref="GetUnderLockAsync"/>); a fetch still running after that finishes in the background and serves the next
/// negotiation. Callers that don't hold a lock (the IPC close) warm it first with <see cref="PrefetchAsync"/>, and a
/// received <c>shutdown</c> starts a fetch without waiting (<see cref="StartFetchIfDue"/>).</para>
/// <para>The host's <see cref="IFeeService"/> is a transient typed HttpClient; this singleton keeps the instance it
/// was built with, so that instance's own cache also works across closes.</para>
/// </remarks>
public sealed class ClosingFeeEstimator
{
    private readonly IFeeService _feeService;
    private readonly ILogger<ClosingFeeEstimator> _logger;
    private readonly ChannelCloseOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();

    private ulong? _estimate;
    private DateTimeOffset _estimatedAt;
    private DateTimeOffset? _failedAt;
    private Task? _fetch;

    public ClosingFeeEstimator(IFeeService feeService, IOptions<ChannelCloseOptions> options,
                               ILogger<ClosingFeeEstimator> logger, TimeProvider? timeProvider = null)
    {
        _feeService = feeService;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The last estimate (sat/kw) obtained, however old, or null when none was.</summary>
    public ulong? Latest
    {
        get
        {
            lock (_gate)
                return _estimate;
        }
    }

    /// <summary>
    /// Makes sure a fresh estimate is fetched, waiting for it (call it without holding a channel lock).
    /// </summary>
    public async Task PrefetchAsync(CancellationToken cancellationToken = default)
    {
        if (StartFetchIfDue() is { } fetch)
            await fetch.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// The estimate for a negotiation that starts under a channel's lock: a fresh one at once, else waits at most
    /// <see cref="ChannelCloseOptions.FeeEstimateWaitUnderLock"/> for a fetch (none during the back-off after a failed
    /// one), then the last value obtained, or null.
    /// </summary>
    public async Task<ulong?> GetUnderLockAsync()
    {
        if (StartFetchIfDue() is { } fetch && _options.FeeEstimateWaitUnderLock > TimeSpan.Zero)
        {
            try
            {
                await fetch.WaitAsync(_options.FeeEstimateWaitUnderLock, _timeProvider);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("No fee estimate within {Wait}; the close starts from the last one",
                                   _options.FeeEstimateWaitUnderLock);
            }
        }

        return Latest;
    }

    /// <summary>
    /// Starts a fetch unless the estimate is fresh, a fetch is running (returned) or the last one failed less than
    /// <see cref="ChannelCloseOptions.FeeEstimateRetryAfter"/> ago; never throws.
    /// </summary>
    /// <returns>The running fetch, or null when none is due.</returns>
    public Task? StartFetchIfDue()
    {
        lock (_gate)
        {
            if (_fetch is not null)
                return _fetch;

            var now = _timeProvider.GetUtcNow();
            if (_estimate is not null && now - _estimatedAt < _options.FeeEstimateMaxAge)
                return null;
            if (_failedAt is { } failedAt && now - failedAt < _options.FeeEstimateRetryAfter)
                return null;

            _fetch = Task.Run(FetchAsync);
            return _fetch;
        }
    }

    private async Task FetchAsync()
    {
        ulong estimate = 0;
        try
        {
            estimate = (ulong)Math.Max(0, (await _feeService.GetFeeRatePerKwAsync()).Satoshi);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Fetching the fee estimate for a close failed");
        }

        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            if (estimate > 0)
            {
                _estimate = estimate;
                _estimatedAt = now;
                _failedAt = null;
            }
            else
            {
                // The fee service logs and swallows its own errors (then answers 0): count that as a failure too
                _failedAt = now;
            }

            _fetch = null;
        }
    }
}