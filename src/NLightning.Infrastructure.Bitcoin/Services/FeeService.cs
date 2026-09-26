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
/// <see cref="FeeRateConverter"/> (NL-288: sat/vB x 250, not x 1000) and is at least 253 sat/kw.
/// </summary>
public class FeeService : IFeeService
{
    private const string FeeCacheFileName = "fee_cache.bin";
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

    /// <summary>
    /// The cached rate (sat/kw in <see cref="LightningMoney.Satoshi"/>), as a new value, so a caller can't change the
    /// cache.
    /// </summary>
    public LightningMoney GetCachedFeeRatePerKw()
    {
        return LightningMoney.Satoshis(Interlocked.Read(ref _cachedFeeRatePerKw));
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
        catch (OperationCanceledException)
        {
            // Ignore cancellation
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error fetching the fee rate from {Source}", _feeEstimationOptions.Source);
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

        var mode = _feeEstimationOptions.EstimateMode.Equals("ECONOMICAL", StringComparison.OrdinalIgnoreCase)
                       ? EstimateSmartFeeMode.Economical
                       : EstimateSmartFeeMode.Conservative;
        var satPerVByte =
            await _bitcoindEstimator(_feeEstimationOptions.ConfirmationTarget, mode, cancellationToken)
         ?? throw new InvalidOperationException(
                $"bitcoind has no fee estimate for {_feeEstimationOptions.ConfirmationTarget} blocks yet.");

        return FeeRateConverter.SatPerVByteToSatPerKw(satPerVByte);
    }

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