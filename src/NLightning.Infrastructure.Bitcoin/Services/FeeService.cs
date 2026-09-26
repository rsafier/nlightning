using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin.RPC;

namespace NLightning.Infrastructure.Bitcoin.Services;

using Domain.Bitcoin.Interfaces;
using Domain.Money;
using Domain.Node.Options;
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
/// Register it as one singleton (<see cref="FeeServiceCollectionExtensions.AddFeeServices"/>): the host starts that
/// instance, and every consumer must read its cache.
/// </remarks>
public class FeeService : IFeeService
{
    private const string FeeCacheFileName = "fee_cache.bin";
    private static readonly string[] s_httpBuckets = ["fastestFee", "halfHourFee", "hourFee", "economyFee"];
    private static readonly TimeSpan s_defaultCacheExpiration = TimeSpan.FromMinutes(5);

    private DateTime _lastFetchTime = DateTime.MinValue;
    private long _cachedFeeRatePerKw;
    private Task? _feeTask;
    private CancellationTokenSource? _cts;

    private readonly HttpClient _httpClient;
    private readonly ILogger<FeeService> _logger;
    private readonly TimeSpan _cacheTimeExpiration;
    private readonly string _cacheFilePath;
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

        // Try to load from the file initially
        _ = LoadFromFileAsync();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

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
            Interlocked.Exchange(ref _cachedFeeRatePerKw, feeRate);
            _lastFetchTime = DateTime.UtcNow;
            await SaveToFileAsync();
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

        try
        {
            if (_feeEstimationOptions.Method.Equals("GET", StringComparison.CurrentCultureIgnoreCase))
            {
                response = await _httpClient.GetAsync(_feeEstimationOptions.Url, cancellationToken);
            }
            else // POST
            {
                var content = new StringContent(
                    _feeEstimationOptions.Body,
                    System.Text.Encoding.UTF8,
                    _feeEstimationOptions.ContentType);

                response = await _httpClient.PostAsync(_feeEstimationOptions.Url, content, cancellationToken);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new InvalidOperationException("Error fetching from API", e);
        }

        response.EnsureSuccessStatusCode();
        var jsonResponseStream = await response.Content.ReadAsStreamAsync(cancellationToken);

        // Parse the JSON response
        using var document =
            await JsonDocument.ParseAsync(jsonResponseStream, cancellationToken: cancellationToken);
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

    private Task SaveToFileAsync()
    {
        _logger.LogDebug("Saving fee rate to file {filePath}", _cacheFilePath);

        return Task.CompletedTask;
        // try
        // {
        //     var cacheData = new FeeRateCacheData
        //     {
        //         FeeRate = _cachedFeeRate,
        //         LastFetchTime = _lastFetchTime
        //     };
        //
        //     await using var fileStream = File.OpenWrite(_cacheFilePath);
        //     await MessagePackSerializer.SerializeAsync(fileStream, cacheData, cancellationToken: CancellationToken.None);
        // }
        // catch (Exception e)
        // {
        //     _logger.LogError(e, "Error saving fee rate to file");
        // }
    }

    private Task LoadFromFileAsync()
    {
        _logger.LogDebug("Loading fee rate from file {filePath}", _cacheFilePath);

        return Task.CompletedTask;
        // try
        // {
        //     if (!File.Exists(_cacheFilePath))
        //     {
        //         _logger.LogDebug("Fee rate cache file does not exist. Skipping load.");
        //         return;
        //     }
        //
        //     await using var fileStream = File.OpenRead(_cacheFilePath);
        //     var cacheData =
        //         await MessagePackSerializer.DeserializeAsync<FeeRateCacheData?>(fileStream,
        //             cancellationToken: cancellationToken);
        //
        //     if (cacheData == null)
        //     {
        //         _logger.LogDebug("Fee rate cache file is empty. Skipping load.");
        //         return;
        //     }
        //
        //     _cachedFeeRate = cacheData.FeeRate;
        //     _lastFetchTime = cacheData.LastFetchTime;
        // }
        // catch (OperationCanceledException)
        // {
        //     // Ignore cancellation
        // }
        // catch (Exception e)
        // {
        //     _logger.LogError(e, "Error loading fee rate from file");
        // }
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

    private static string ParseFilePath(FeeEstimationOptions feeEstimationOptions)
    {
        var filePath = feeEstimationOptions.CacheFile;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FeeCacheFileName);
        }

        // Check if the file path is absolute or relative
        return Path.IsPathRooted(filePath)
                   ? filePath
                   : Path.Combine(Directory.GetCurrentDirectory(),
                                  filePath); // If it's relative, combine it with the current directory
    }
}