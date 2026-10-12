using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Bitcoin.Accounting.Prices;

using Domain.Accounting.Financial;
using Domain.Accounting.Prices;

/// <summary>
/// The operator's price file (<c>Accounting:Prices:CsvFile</c>, default <c>&lt;configPath&gt;/prices.csv</c>; D-A11,
/// NL-602 A3-T2) as a price source. The file is read on the first ask and again whenever its modification time or
/// length changes; its bad lines are logged by line number (the good ones are used). The file holds prices in the
/// configured currency only. A missing file is an empty source.
/// </summary>
public sealed class CsvPriceSource : IPriceSource
{
    private const int MaxLoggedErrors = 10;

    private readonly Lock _gate = new();
    private readonly ILogger<CsvPriceSource> _logger;
    private readonly AccountingPriceOptions _options;
    private readonly TimeProvider _timeProvider;

    private (DateTime ModifiedUtc, long Length)? _loadedStamp;
    private IReadOnlyList<AccountingPricePoint> _points = [];
    private bool _reportedMissing;

    public CsvPriceSource(IOptions<AccountingPriceOptions> options, ILogger<CsvPriceSource> logger,
                          TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The file's full path.</summary>
    public string FilePath => Path.GetFullPath(_options.CsvFile);

    /// <summary>How many times the file was read (tests).</summary>
    internal int LoadCount { get; private set; }

    /// <inheritdoc />
    public Task<AccountingPrice?> GetPriceAsync(string currency, DateTimeOffset time,
                                                CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(currency?.Trim(), _options.NormalizedCurrency, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<AccountingPrice?>(null);

        var points = Load();
        var point = AccountingValuation.NearestAtOrBefore(points, p => p.Time, time, TimeSpan.MaxValue);
        return Task.FromResult(point is null
                                   ? null
                                   : new AccountingPrice(0, _options.NormalizedCurrency, point.Time, point.Price,
                                                         AccountingPriceSource.Csv, _timeProvider.GetUtcNow()));
    }

    private IReadOnlyList<AccountingPricePoint> Load()
    {
        lock (_gate)
        {
            var path = FilePath;
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                if (!_reportedMissing)
                {
                    _logger.LogInformation("No price file at {Path}; the price file source has no prices", path);
                    _reportedMissing = true;
                }

                _loadedStamp = null;
                _points = [];
                return _points;
            }

            _reportedMissing = false;
            var stamp = (info.LastWriteTimeUtc, info.Length);
            if (_loadedStamp == stamp)
                return _points;

            try
            {
                using var reader = new StreamReader(path);
                var result = AccountingPriceCsv.Parse(reader);
                LoadCount++;
                _points = result.Points;
                _loadedStamp = stamp;
                if (!result.IsValid)
                {
                    _logger.LogWarning("The price file {Path} has {Count} bad line(s), left out: {Errors}", path,
                                       result.ErrorCount,
                                       string.Join("; ", result.Errors.Take(MaxLoggedErrors)));
                }

                _logger.LogInformation("Read {Count} prices from {Path}", _points.Count, path);
            }
            catch (IOException e)
            {
                // Being written: keep what we had and read it again on the next ask
                _logger.LogWarning(e, "Could not read the price file {Path}", path);
            }
            catch (UnauthorizedAccessException e)
            {
                _logger.LogWarning(e, "Could not read the price file {Path}", path);
            }

            return _points;
        }
    }
}