using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin.RPC;

namespace NLightning.Infrastructure.Bitcoin.Services;

using Domain.Bitcoin.Interfaces;
using Domain.Money;
using Domain.Node.Options;
using Infrastructure.Transport.Http;
using Networks;
using Options;

/// <summary>
/// The node's fee estimate in sat/kw, from the source <see cref="FeeEstimationOptions.Source"/> selects: a
/// mempool.space-style HTTP API (default), bitcoind's <c>estimatesmartfee</c> or a fixed rate. Every rate goes through
/// <see cref="FeeRateConverter"/> (NL-288: sat/vB x 250, not x 1000) and is at least 253 sat/kw. Without any estimate
/// the rate is <see cref="FeeEstimationOptions.FallbackFeeRatePerKw"/>, never 0.
/// </summary>
/// <remarks>
/// <para>Per confirmation target (<see cref="GetFeeRatePerKwAsync(uint, CancellationToken)"/>, BOLT 5 plan O6-T1,
/// NL-296): <see cref="FeeEstimationOptions.SourceBitcoind"/> asks <c>estimatesmartfee</c> for that target (cached per
/// target like the node-wide rate); <see cref="FeeEstimationOptions.SourceHttp"/> picks the mempool.space bucket of the
/// last response that fits the target (<see cref="GetHttpBucket"/>); <see cref="FeeEstimationOptions.SourceFixed"/> is
/// the fixed rate. Without a per-target estimate the node-wide rate is answered.</para>
/// <para>The last good estimate (node-wide rate and HTTP buckets) is saved to <see cref="FeeEstimationOptions.CacheFile"/>
/// after each successful fetch, by a background writer (never on the caller's path), and read back when the service is
/// built (NL-706): at <see cref="StartAsync"/> a saved estimate younger than
/// <see cref="FeeEstimationOptions.CacheExpiration"/> is used without waiting for a fetch, and one younger than
/// <see cref="FeeEstimationOptions.CacheMaxAge"/> replaces <see cref="FeeEstimationOptions.FallbackFeeRatePerKw"/> while
/// the first fetch fails. An older, corrupt or other-source file is ignored and logged.</para>
/// Register it as one singleton (<see cref="FeeServiceCollectionExtensions.AddFeeServices"/>): the host starts that
/// instance, and every consumer must read its cache.
/// </remarks>
public class FeeService : IFeeService
{
    /// <summary>The longest HTTP answer read (64 KiB, NL-678): a mempool.space fee answer is about 100 bytes.</summary>
    public const int MaxResponseBytes = HttpResponseLimits.SmallResponseMaxBytes;

    private static readonly string[] s_httpBuckets = ["fastestFee", "halfHourFee", "hourFee", "economyFee"];
    private static readonly TimeSpan s_defaultCacheExpiration = TimeSpan.FromMinutes(5);

    /// <summary>How long <see cref="StartAsync"/> waits for the cache file read before going on without it.</summary>
    private static readonly TimeSpan s_cacheLoadWait = TimeSpan.FromSeconds(2);

    /// <summary>How long <see cref="StopAsync"/> waits for the last cache write.</summary>
    private static readonly TimeSpan s_cacheSaveWait = TimeSpan.FromSeconds(5);

    /// <summary>The highest rate taken from the cache file: a corruption guard, not a fee policy.</summary>
    internal const long MaxCachedFeeRatePerKw = int.MaxValue;

    /// <summary>A saved estimate this far in the future (clock change) is still taken, as fetched now.</summary>
    private static readonly TimeSpan s_clockSkewTolerance = TimeSpan.FromMinutes(5);

    private DateTime _lastFetchTime = DateTime.MinValue;
    private long _cachedFeeRatePerKw;
    private Task? _feeTask;
    private CancellationTokenSource? _cts;

    private readonly HttpClient _httpClient;
    private readonly ILogger<FeeService> _logger;
    private readonly TimeSpan _cacheTimeExpiration;
    private readonly string? _cacheFilePath;
    private readonly TimeSpan _cacheMaxAge;
    private readonly string _cacheSourceKey;
    private readonly Lock _stateLock = new();
    private readonly Lock _saveLock = new();
    private readonly Task _cacheLoadTask = Task.CompletedTask;
    private bool _fetchedOnce;
    private FeeRateCacheEntry? _pendingSave;
    private Task _saveTask = Task.CompletedTask;
    private readonly FeeEstimationOptions _feeEstimationOptions;
    private readonly Func<int, EstimateSmartFeeMode, CancellationToken, Task<decimal?>>? _bitcoindEstimator;
    private readonly ConcurrentDictionary<uint, (long FeeRatePerKw, DateTime FetchedAt)> _targetCache = new();
    private volatile IReadOnlyDictionary<string, long> _httpBuckets = new Dictionary<string, long>();

    /// <remarks>
    /// <paramref name="bitcoinOptions"/> and <paramref name="nodeOptions"/> are only used by
    /// <see cref="FeeEstimationOptions.SourceBitcoind"/>, which talks to bitcoind with its own RPC client, created on the
    /// first estimate.
    /// </remarks>
    public FeeService(IOptions<FeeEstimationOptions> feeOptions, HttpClient httpClient, ILogger<FeeService> logger,
                      IOptions<BitcoinOptions>? bitcoinOptions = null, IOptions<NodeOptions>? nodeOptions = null)
        : this(feeOptions.Value, httpClient, logger, CreateBitcoindEstimator(bitcoinOptions, nodeOptions))
    {
    }

    /// <param name="feeOptions">The fee source settings; invalid settings throw.</param>
    /// <param name="httpClient">The client for <see cref="FeeEstimationOptions.SourceHttp"/>.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="bitcoindEstimator">For <see cref="FeeEstimationOptions.SourceBitcoind"/>: bitcoind's
    /// <c>estimatesmartfee</c> in sat/vB for a confirmation target and mode, or null when it has no estimate.</param>
    /// <exception cref="InvalidOperationException">The options are invalid.</exception>
    internal FeeService(FeeEstimationOptions feeOptions, HttpClient httpClient, ILogger<FeeService> logger,
                        Func<int, EstimateSmartFeeMode, CancellationToken, Task<decimal?>>? bitcoindEstimator)
    {
        _feeEstimationOptions = feeOptions;
        _httpClient = httpClient;
        _logger = logger;
        _bitcoindEstimator = bitcoindEstimator;

        var errors = _feeEstimationOptions.GetValidationErrors();
        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid FeeEstimation configuration: " + string.Join(" ", errors));

        if (!string.IsNullOrWhiteSpace(_feeEstimationOptions.RateMultiplier) && _logger.IsEnabled(LogLevel.Warning))
            _logger.LogWarning("FeeEstimation:RateMultiplier ({RateMultiplier}) is ignored: the HTTP value is read in "
                             + "FeeEstimation:RateUnit ({RateUnit}) and converted to sat/kw (NL-288)",
                               _feeEstimationOptions.RateMultiplier, _feeEstimationOptions.RateUnit);

        _cacheFilePath = ParseFilePath(_feeEstimationOptions);
        _cacheTimeExpiration = ParseCacheTime(_feeEstimationOptions.CacheExpiration);
        _cacheMaxAge = FeeEstimationOptions.TryParseDuration(_feeEstimationOptions.CacheMaxAge, out var maxAge)
                           ? maxAge
                           : TimeSpan.FromHours(1);
        _cacheSourceKey = ComputeSourceKey(_feeEstimationOptions);

        // Read the saved estimate off the constructing thread; StartAsync waits for it, bounded (NL-706)
        if (_cacheFilePath is not null)
            _cacheLoadTask = Task.Run(LoadFromFile);
    }

    /// <summary>The cache file in use, as a full path, or null when there is none (off, or a fixed rate).</summary>
    internal string? CacheFilePath => _cacheFilePath;

    /// <summary>Completes when the cache file was read at construction (tests).</summary>
    internal Task CacheLoaded => _cacheLoadTask;

    /// <summary>Completes when every cache write scheduled so far is done (tests).</summary>
    internal Task FlushCacheAsync()
    {
        lock (_saveLock)
            return _saveTask;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // The saved estimate, if the file could be read in time: a slow disk never holds the start (NL-706)
        try
        {
            await _cacheLoadTask.WaitAsync(s_cacheLoadWait, cancellationToken);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Reading the fee rate cache file {CacheFile} takes more than {Wait}; starting without it",
                               _cacheFilePath, s_cacheLoadWait);
        }

        // Start the background task
        _feeTask = RunPeriodicRefreshAsync(_cts.Token);

        // If the cache from the file is not valid, refresh immediately
        if (!IsCacheValid())
        {
            await RefreshFeeRateAsync(_cts.Token);
        }
    }

    public async Task StopAsync()
    {
        if (_cts is null)
        {
            throw new InvalidOperationException("Service is not running");
        }

        await _cts.CancelAsync();

        if (_feeTask is not null)
        {
            try
            {
                await _feeTask;
            }
            catch (OperationCanceledException)
            {
                // Expected during cancellation
            }
        }

        // The last estimate reaches the file before the process ends, bounded so a stuck disk never holds the stop
        try
        {
            await FlushCacheAsync().WaitAsync(s_cacheSaveWait);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Writing the fee rate cache file {CacheFile} takes more than {Wait}; stopping without it",
                               _cacheFilePath, s_cacheSaveWait);
        }
    }

    public async Task<LightningMoney> GetFeeRatePerKwAsync(CancellationToken cancellationToken = default)
    {
        if (IsCacheValid())
        {
            return GetCachedFeeRatePerKw();
        }

        using var linkedCts = CancellationTokenSource
           .CreateLinkedTokenSource(cancellationToken, _cts?.Token ?? CancellationToken.None);

        await RefreshFeeRateAsync(linkedCts.Token);
        return GetCachedFeeRatePerKw();
    }

    /// <inheritdoc />
    public async Task<LightningMoney> GetFeeRatePerKwAsync(uint confirmationTarget,
                                                           CancellationToken cancellationToken = default)
    {
        var target = Math.Clamp(confirmationTarget, 1u, MaxConfirmationTarget);
        if (_feeEstimationOptions.IsSource(FeeEstimationOptions.SourceBitcoind) && _bitcoindEstimator is not null)
        {
            if (_targetCache.TryGetValue(target, out var cached)
             && DateTime.UtcNow - cached.FetchedAt <= _cacheTimeExpiration)
                return LightningMoney.Satoshis(cached.FeeRatePerKw);

            try
            {
                var satPerVByte = await _bitcoindEstimator((int)target, GetEstimateMode(), cancellationToken);
                if (satPerVByte is { } rate)
                {
                    var perKw = FeeRateConverter.SatPerVByteToSatPerKw(rate);
                    _targetCache[target] = (perKw, DateTime.UtcNow);
                    return LightningMoney.Satoshis(perKw);
                }

                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("bitcoind has no fee estimate for {Target} blocks; using the node-wide rate",
                                     target);
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(e, "Fetching the fee rate for {Target} blocks from bitcoind failed; using the "
                                    + "node-wide rate", target);
            }

            return await GetFeeRatePerKwAsync(cancellationToken);
        }

        var nodeWide = await GetFeeRatePerKwAsync(cancellationToken);
        if (!_feeEstimationOptions.IsSource(FeeEstimationOptions.SourceHttp))
            return nodeWide;

        var buckets = _httpBuckets;
        return GetHttpBucket(target) is { } bucket && buckets.TryGetValue(bucket, out var bucketRate)
                   ? LightningMoney.Satoshis(bucketRate)
                   : nodeWide;
    }

    /// <summary>
    /// The mempool.space bucket for a confirmation target: <c>fastestFee</c> (next block) for 1, <c>halfHourFee</c> up to
    /// 3, <c>hourFee</c> up to 6 and below <see cref="MaxConfirmationTarget"/> (a slow sweep still wants to confirm
    /// within hours, not days), <c>economyFee</c> from <see cref="MaxConfirmationTarget"/> on.
    /// </summary>
    internal static string? GetHttpBucket(uint confirmationTarget) => confirmationTarget switch
    {
        0 => null,
        1 => "fastestFee",
        <= 3 => "halfHourFee",
        < MaxConfirmationTarget => "hourFee",
        _ => "economyFee"
    };

    /// <summary>The largest confirmation target asked for (a day of blocks).</summary>
    internal const uint MaxConfirmationTarget = 144;

    /// <summary>
    /// The cached rate (sat/kw in <see cref="LightningMoney.Satoshi"/>), as a new value, so a caller can't change the
    /// cache.
    /// </summary>
    /// <remarks>
    /// Until the first successful estimate (not started yet, or a source without an estimate, such as bitcoind's
    /// <c>estimatesmartfee</c> on a young signet) this is <see cref="FeeEstimationOptions.FallbackFeeRatePerKw"/>, never
    /// 0: a rate of 0 would put <c>feerate_per_kw=0</c> in open_channel and make every dust fee 0.
    /// </remarks>
    public LightningMoney GetCachedFeeRatePerKw()
    {
        var cached = Interlocked.Read(ref _cachedFeeRatePerKw);
        return LightningMoney.Satoshis(cached > 0 ? cached : _feeEstimationOptions.FallbackFeeRatePerKw);
    }

    public async Task RefreshFeeRateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var feeRate = await FetchFeeRatePerKwAsync(cancellationToken);
            var fetchedAt = DateTime.UtcNow;
            lock (_stateLock)
            {
                Interlocked.Exchange(ref _cachedFeeRatePerKw, feeRate);
                _lastFetchTime = fetchedAt;
                _fetchedOnce = true;
            }

            ScheduleSave(feeRate, fetchedAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Our own cancellation (stopping, or the caller gave up): nothing to report
        }
        catch (Exception e)
        {
            // An HttpClient timeout is an OperationCanceledException although our token is not cancelled: log it too
            var reason = e is OperationCanceledException ? "timed out" : "failed";
            if (Interlocked.Read(ref _cachedFeeRatePerKw) > 0)
            {
                _logger.LogError(e, "Fetching the fee rate from {Source} {Reason}; keeping the last estimate",
                                 _feeEstimationOptions.Source, reason);
            }
            else if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(e, "Fetching the fee rate from {Source} {Reason} and there is no estimate yet; "
                                    + "using FeeEstimation:FallbackFeeRatePerKw ({FallbackFeeRatePerKw} sat/kw)",
                                   _feeEstimationOptions.Source, reason, _feeEstimationOptions.FallbackFeeRatePerKw);
            }
        }
    }

    private async Task<long> FetchFeeRatePerKwAsync(CancellationToken cancellationToken)
    {
        if (_feeEstimationOptions.IsSource(FeeEstimationOptions.SourceFixed))
            return Math.Max(FeeRateConverter.FeeratePerKwFloor, _feeEstimationOptions.FixedFeeRatePerKw);

        if (_feeEstimationOptions.IsSource(FeeEstimationOptions.SourceBitcoind))
            return await FetchFeeRateFromBitcoindAsync(cancellationToken);

        return await FetchFeeRateFromApiAsync(cancellationToken);
    }

    private async Task<long> FetchFeeRateFromBitcoindAsync(CancellationToken cancellationToken)
    {
        if (_bitcoindEstimator is null)
            throw new InvalidOperationException(
                "FeeEstimation:Source is Bitcoind, but the Bitcoin RPC settings or the node's network are missing.");

        var satPerVByte =
            await _bitcoindEstimator(_feeEstimationOptions.ConfirmationTarget, GetEstimateMode(), cancellationToken)
         ?? throw new InvalidOperationException(
                $"bitcoind has no fee estimate for {_feeEstimationOptions.ConfirmationTarget} blocks yet.");

        return FeeRateConverter.SatPerVByteToSatPerKw(satPerVByte);
    }

    private EstimateSmartFeeMode GetEstimateMode() =>
        _feeEstimationOptions.EstimateMode.Equals("ECONOMICAL", StringComparison.OrdinalIgnoreCase)
            ? EstimateSmartFeeMode.Economical
            : EstimateSmartFeeMode.Conservative;

    private async Task<long> FetchFeeRateFromApiAsync(CancellationToken cancellationToken)
    {
        HttpResponseMessage response;

        // HttpClient.Timeout stops at the headers with ResponseHeadersRead, so one deadline covers the request and the
        // bounded body read: a server or Tor circuit that sends the headers and then stalls the body times out like a
        // request would (NL-732), instead of holding the start, the refresh loop and every caller that waits on it
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_httpClient.Timeout != Timeout.InfiniteTimeSpan)
            deadline.CancelAfter(_httpClient.Timeout);

        try
        {
            // ResponseHeadersRead: the body is read bounded below (NL-678), never buffered whole by the client
            if (_feeEstimationOptions.Method.Equals("GET", StringComparison.CurrentCultureIgnoreCase))
            {
                response = await _httpClient.GetAsync(_feeEstimationOptions.Url,
                                                      HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            }
            else // POST
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _feeEstimationOptions.Url);
                request.Content = new StringContent(
                    _feeEstimationOptions.Body,
                    System.Text.Encoding.UTF8,
                    _feeEstimationOptions.ContentType);

                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                                                       deadline.Token);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new InvalidOperationException("Error fetching from API", e);
        }

        byte[] body;
        using (response)
        {
            response.EnsureSuccessStatusCode();
            body = await HttpResponseLimits.ReadBoundedAsync(response.Content, MaxResponseBytes, deadline.Token);
        }

        // Parse the JSON response
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        // Extract the preferred fee rate from the JSON response
        if (!root.TryGetProperty(_feeEstimationOptions.PreferredFeeRate, out var feeRateElement)
         || !feeRateElement.TryGetDecimal(out var feeRate))
        {
            throw new InvalidOperationException(
                $"Could not extract {_feeEstimationOptions.PreferredFeeRate} from API response.");
        }

        // The other buckets of the same response, for the per-target estimates (NL-296)
        var buckets = new Dictionary<string, long>();
        foreach (var bucket in s_httpBuckets)
        {
            if (root.TryGetProperty(bucket, out var element) && element.TryGetDecimal(out var bucketRate))
                buckets[bucket] = FeeRateConverter.ToSatPerKw(bucketRate, _feeEstimationOptions.RateUnit);
        }

        // Under the state lock: the cache file read must not put older buckets over these (NL-763)
        lock (_stateLock)
            _httpBuckets = buckets;

        // From the API's unit (sat/vB for mempool.space) to sat/kw (NL-288)
        return FeeRateConverter.ToSatPerKw(feeRate, _feeEstimationOptions.RateUnit);
    }

    private static Func<int, EstimateSmartFeeMode, CancellationToken, Task<decimal?>>? CreateBitcoindEstimator(
        IOptions<BitcoinOptions>? bitcoinOptions, IOptions<NodeOptions>? nodeOptions)
    {
        if (bitcoinOptions is null || nodeOptions is null)
            return null;

        RPCClient? rpcClient = null;
        return async (confirmationTarget, mode, cancellationToken) =>
        {
            // Created on first use, so a node that estimates another way never reads these settings here
            rpcClient ??= new RPCClient(
                new RPCCredentialString
                {
                    UserPassword =
                        new NetworkCredential(bitcoinOptions.Value.RpcUser, bitcoinOptions.Value.RpcPassword)
                }, bitcoinOptions.Value.RpcEndpoint, nodeOptions.Value.BitcoinNetwork.ToNBitcoinNetwork());

            var response = await rpcClient.TryEstimateSmartFeeAsync(confirmationTarget, mode, cancellationToken);
            return response?.FeeRate.SatoshiPerByte;
        };
    }

    private async Task RunPeriodicRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Refresh if it's not canceled
                if (!cancellationToken.IsCancellationRequested)
                {
                    await RefreshFeeRateAsync(cancellationToken);

                    // Wait for the cache time or until cancellation
                    await Task.Delay(_cacheTimeExpiration, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Stopping fee service");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in fee service");
        }
    }

    /// <summary>
    /// Queues the estimate for the cache file. One write runs at a time, in order, and a write takes the newest queued
    /// estimate, so a burst of refreshes costs one or two writes; the caller never waits on the disk (NL-706).
    /// </summary>
    private void ScheduleSave(long feeRatePerKw, DateTime fetchedAt)
    {
        if (_cacheFilePath is null)
            return;

        var entry = new FeeRateCacheEntry
        {
            Source = _feeEstimationOptions.Source.Trim(),
            SourceKey = _cacheSourceKey,
            FetchedAt = new DateTimeOffset(fetchedAt, TimeSpan.Zero),
            FeeRatePerKw = feeRatePerKw,
            Buckets = _feeEstimationOptions.IsSource(FeeEstimationOptions.SourceHttp)
                          ? new Dictionary<string, long>(_httpBuckets)
                          : null
        };

        lock (_saveLock)
        {
            var writeQueued = _pendingSave is not null;
            _pendingSave = entry;
            if (!writeQueued)
                _saveTask = _saveTask.ContinueWith(_ => WritePending(), CancellationToken.None,
                                                   TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    private void WritePending()
    {
        FeeRateCacheEntry? entry;
        lock (_saveLock)
        {
            entry = _pendingSave;
            _pendingSave = null;
        }

        if (entry is null || _cacheFilePath is null)
            return;

        try
        {
            FeeRateCacheFile.Write(_cacheFilePath, entry);
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Saved the fee rate {FeeRatePerKw} sat/kw to {CacheFile}", entry.FeeRatePerKw,
                                 _cacheFilePath);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Saving the fee rate to {CacheFile} failed; the next estimate tries again",
                               _cacheFilePath);
        }
    }

    /// <summary>
    /// Reads the saved estimate and takes it unless a fetch already answered: stale (older than
    /// <see cref="FeeEstimationOptions.CacheMaxAge"/>), corrupt, other-source and unreadable files are logged and ignored.
    /// </summary>
    private void LoadFromFile()
    {
        if (_cacheFilePath is null)
            return;

        try
        {
            var entry = FeeRateCacheFile.TryRead(_cacheFilePath, out var problem);
            if (entry is null)
            {
                if (problem is not null)
                    _logger.LogWarning("Ignoring the fee rate cache file {CacheFile}: {Problem}", _cacheFilePath,
                                       problem);
                else if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("No fee rate cache file at {CacheFile} yet", _cacheFilePath);
                return;
            }

            if (GetEntryProblem(entry, DateTime.UtcNow) is { } entryProblem)
            {
                _logger.LogWarning("Ignoring the fee rate cache file {CacheFile}: {Problem}", _cacheFilePath,
                                   entryProblem);
                return;
            }

            var now = DateTime.UtcNow;
            var fetchedAt = entry.FetchedAt.UtcDateTime > now ? now : entry.FetchedAt.UtcDateTime;
            lock (_stateLock)
            {
                if (_fetchedOnce)
                    return; // a fetch was quicker than the file: it is newer

                Interlocked.Exchange(ref _cachedFeeRatePerKw, entry.FeeRatePerKw);
                _lastFetchTime = fetchedAt;
                // A fetch stores its buckets before it marks _fetchedOnce: buckets already there are newer (NL-763)
                if (entry.Buckets is not null && _feeEstimationOptions.IsSource(FeeEstimationOptions.SourceHttp)
                                              && _httpBuckets.Count == 0)
                    _httpBuckets = new Dictionary<string, long>(entry.Buckets);
            }

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Loaded the fee rate {FeeRatePerKw} sat/kw fetched at {FetchedAt:u} from "
                                     + "{CacheFile}", entry.FeeRatePerKw, fetchedAt, _cacheFilePath);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Reading the fee rate cache file {CacheFile} failed; starting without it",
                               _cacheFilePath);
        }
    }

    /// <summary>Why a saved entry can't be used now, or null when it can.</summary>
    private string? GetEntryProblem(FeeRateCacheEntry entry, DateTime now)
    {
        if (!string.Equals(entry.Source, _feeEstimationOptions.Source.Trim(), StringComparison.OrdinalIgnoreCase)
         || !string.Equals(entry.SourceKey, _cacheSourceKey, StringComparison.Ordinal))
            return $"it was saved for another fee source ({entry.Source}) or other source settings";

        if (entry.FeeRatePerKw < FeeRateConverter.FeeratePerKwFloor)
            return $"its rate {entry.FeeRatePerKw} sat/kw is below {FeeRateConverter.FeeratePerKwFloor} sat/kw";

        if (entry.Buckets?.Any(b => b.Value < FeeRateConverter.FeeratePerKwFloor) == true)
            return $"a bucket rate is below {FeeRateConverter.FeeratePerKwFloor} sat/kw";

        // Only a damaged file holds such a value (it would overflow the msat amounts built from it)
        if (entry.FeeRatePerKw > MaxCachedFeeRatePerKw
         || entry.Buckets?.Any(b => b.Value > MaxCachedFeeRatePerKw) == true)
            return $"a rate is above {MaxCachedFeeRatePerKw} sat/kw";

        var fetchedAt = entry.FetchedAt.UtcDateTime;
        if (fetchedAt > now + s_clockSkewTolerance)
            return $"it was fetched at {fetchedAt:u}, in the future";

        var age = now - fetchedAt;
        if (age > _cacheMaxAge)
            return $"it was fetched at {fetchedAt:u}, more than FeeEstimation:CacheMaxAge ({_cacheMaxAge}) ago";

        return null;
    }

    /// <summary>The settings that shape an estimate, hashed (a POST body may carry an API key).</summary>
    internal static string ComputeSourceKey(FeeEstimationOptions options)
    {
        var settings = options.IsSource(FeeEstimationOptions.SourceBitcoind)
                           ? $"{options.ConfirmationTarget}\n{options.EstimateMode.ToUpperInvariant()}"
                           : $"{options.Url}\n{options.Method.ToUpperInvariant()}\n{options.Body}\n"
                           + $"{options.PreferredFeeRate}\n{options.RateUnit}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(settings)));
    }

    private bool IsCacheValid()
    {
        return Interlocked.Read(ref _cachedFeeRatePerKw) > 0 && DateTime.UtcNow.Subtract(_lastFetchTime).CompareTo(_cacheTimeExpiration) <= 0;
    }

    private static TimeSpan ParseCacheTime(string cacheTime)
    {
        try
        {
            // Parse formats like "5m", "1hour", "30s"
            var valueStr = new string(cacheTime.Where(char.IsDigit).ToArray());
            var unit = new string(cacheTime.Where(char.IsLetter).ToArray()).ToLowerInvariant();

            if (!int.TryParse(valueStr, out var value))
                return s_defaultCacheExpiration;

            return unit switch
            {
                "s" or "second" or "seconds" => TimeSpan.FromSeconds(value),
                "m" or "minute" or "minutes" => TimeSpan.FromMinutes(value),
                "h" or "hour" or "hours" => TimeSpan.FromHours(value),
                "d" or "day" or "days" => TimeSpan.FromDays(value),
                _ => TimeSpan.FromMinutes(5)
            };
        }
        catch
        {
            return s_defaultCacheExpiration; // Default on error
        }
    }

    /// <summary>
    /// The cache file as a full path (a relative one from the working directory; the daemon has already anchored the
    /// file's relative path to the configuration directory, NL-306), or null when the cache is off: no
    /// <see cref="FeeEstimationOptions.CacheFile"/>, or <see cref="FeeEstimationOptions.SourceFixed"/>, which has
    /// nothing to remember.
    /// </summary>
    private static string? ParseFilePath(FeeEstimationOptions feeEstimationOptions)
    {
        var filePath = feeEstimationOptions.CacheFile;
        if (string.IsNullOrWhiteSpace(filePath) || feeEstimationOptions.IsSource(FeeEstimationOptions.SourceFixed))
            return null;

        return Path.GetFullPath(filePath.Trim());
    }
}