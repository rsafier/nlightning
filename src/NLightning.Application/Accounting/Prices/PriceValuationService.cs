using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Accounting.Prices;

using Domain.Accounting.Books;
using Domain.Accounting.Financial;
using Domain.Accounting.Prices;
using Domain.Persistence.Interfaces;

/// <summary>
/// The back-valuation of the financial books (NL-602 A3-T2, plan <c>docs/agents/ACCOUNTING_PLAN.md</c> §6.2, D-A8,
/// D-A11): a timer loop, off every hot path, that gives the financial book's postings without a fiat value the price
/// of their time, and the operator's <c>prices import|list|fetch</c> (IPC 45).
/// </summary>
/// <remarks>
/// <para><b>A round</b> (every <see cref="AccountingPriceOptions.FetchInterval"/>, once at <see cref="Start"/>, early
/// on <see cref="Nudge"/>, and on <see cref="ValueNowAsync"/>) pages through the unvalued postings of the financial
/// book (<see cref="IAccountingBooksDbRepository.ListUnvaluedPostingsAsync(AccountingBook, AccountingUnvaluedPostingCursor?, int, CancellationToken)"/>,
/// oldest first, <see cref="PageSize"/> per page and save). Each posting takes the nearest stored price at or before
/// its time no older than <see cref="AccountingPriceOptions.MaxAge"/> (<see cref="AccountingValuation"/>). With a source
/// configured, a posting whose own UTC hour holds no stored price yet waits for it: the waiting postings are grouped by
/// hour and each hour is asked of the <see cref="IPriceSource"/> once (at its earliest posting's time), at most
/// <see cref="AccountingPriceOptions.MaxFetchesPerRound"/> asks per round (the rest wait for the next round,
/// <see cref="AccountingValuationRoundResult.Deferred"/>); what the source answers is stored and saved first (a
/// price's id exists only after its save), then the page's values are staged
/// (<see cref="IAccountingBooksDbRepository.SetPostingValueAsync"/>) and saved in one save. An hour the source had no
/// price of its own for is not asked again for <see cref="RetryDelay"/>, and its postings take the nearest price within
/// <c>MaxAge</c> meanwhile (once the hour is <see cref="RecentHourWait"/> old). With <see cref="AccountingPriceSourceMode.None"/> (or <c>MaxFetchesPerRound</c> 0) no
/// source is asked at all and the stored prices (imports) value at once.</para>
/// <para><b>Closed periods</b> (D-A8): a posting whose entry is in a closed period (its <c>ClosedPeriodId</c>, or a
/// closed <see cref="AccountingPeriod"/> that holds its time) is never filled. When a price is found for it, it is
/// handed to the <see cref="IAccountingAdjustmentSink"/> (A3-T5's rule, staged on the page's unit of work); the
/// default sink takes nothing and the posting stays unvalued. To keep rounds cheap, a routine round starts after the
/// last closed period; a full pass from the beginning runs at the first round, every <see cref="FullPassInterval"/>,
/// when the closed periods change and after an import or a fetch.</para>
/// <para>Books off (<see cref="AccountingOptions.AreBooksEnabled"/> false) or invalid <c>Accounting:Prices</c>: the
/// loop never starts (logged); an exception in a round is logged and metered and only that round stops.</para>
/// </remarks>
public sealed class PriceValuationService : IAccountingPrices, IAsyncDisposable, IDisposable
{
    /// <summary>The default <see cref="PageSize"/>.</summary>
    public const int DefaultPageSize = 500;

    /// <summary>The most pages one round looks at; the next round continues from the start.</summary>
    public const int MaxPagesPerRound = 100;

    /// <summary>The most stored prices a page loads to value with in memory; past it, one lookup per posting.</summary>
    public const int PriceWindowCap = 20_000;

    /// <summary>The most prices one import stores.</summary>
    public const int MaxImportRows = 100_000;

    /// <summary>The prices one import save stores.</summary>
    public const int ImportBatchSize = 1_000;

    /// <summary>The most prices <see cref="ListAsync"/> returns.</summary>
    public const int MaxListTake = 1_000;

    private const int MaxRememberedHours = 10_000;
    private const int MaxRememberedAdjustments = 100_000;

    /// <summary>How often a routine round is a full pass from the beginning.</summary>
    public static readonly TimeSpan FullPassInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// How long after an hour starts its postings wait for that hour's own price even when the source answered an
    /// older one (a source publishes an hour's price after the hour began); later, the nearest price within
    /// <c>MaxAge</c> values them.
    /// </summary>
    public static readonly TimeSpan RecentHourWait = TimeSpan.FromHours(2);

    /// <summary>The default interval when <see cref="AccountingPriceOptions.FetchInterval"/> is not positive.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(10);

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _roundGate = new(1, 1);
    private readonly ILogger<PriceValuationService> _logger;
    private readonly AccountingOptions _accountingOptions;
    private readonly AccountingPriceOptions _priceOptions;
    private readonly IReadOnlyList<string> _optionErrors;
    private readonly IPriceSource? _priceSource;
    private readonly IAccountingAdjustmentSink _adjustmentSink;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Dictionary<DateTimeOffset, DateTimeOffset> _retryAfter = [];
    private readonly HashSet<AccountingPostingKey> _adjusted = [];
    private readonly Counter<long> _fetchedCounter;
    private readonly Counter<long> _storedCounter;
    private readonly Counter<long> _valuedCounter;
    private readonly Counter<long> _lateCounter;
    private readonly Counter<long> _failureCounter;

    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _loop;
    private bool _started;
    private bool _stopped;
    private bool _fullPassDue = true;
    private DateTimeOffset _nextFullPass = DateTimeOffset.MinValue;
    private string? _closedSignature;
    private bool _reportedClosedLeft;
    private long _totalValued;
    private long _totalFetched;

    public PriceValuationService(IServiceScopeFactory scopeFactory, ILogger<PriceValuationService> logger,
                                 IOptions<AccountingOptions>? accountingOptions = null,
                                 IOptions<AccountingPriceOptions>? priceOptions = null,
                                 IPriceSource? priceSource = null, IAccountingAdjustmentSink? adjustmentSink = null,
                                 TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _scopeFactory = scopeFactory;
        _logger = logger;
        _accountingOptions = accountingOptions?.Value ?? new AccountingOptions();
        _priceOptions = priceOptions?.Value ?? new AccountingPriceOptions();
        _optionErrors = _priceOptions.GetValidationErrors();
        _priceSource = priceSource;
        _adjustmentSink = adjustmentSink ?? NullAccountingAdjustmentSink.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;

        Meter = new Meter(AccountingEventSealerService.MeterName);
        _fetchedCounter = Meter.CreateCounter<long>("nlightning.accounting.prices.fetched", "{request}",
                                                    "Prices the back-valuation asked its sources for");
        _storedCounter = Meter.CreateCounter<long>("nlightning.accounting.prices.stored", "{price}",
                                                   "Prices stored (fetched or imported)");
        _valuedCounter = Meter.CreateCounter<long>("nlightning.accounting.postings.valued", "{posting}",
                                                   "Financial postings given a fiat value");
        _lateCounter = Meter.CreateCounter<long>("nlightning.accounting.valuation.late", "{posting}",
                                                 "Postings of a closed period handed to the adjustment rule");
        _failureCounter = Meter.CreateCounter<long>("nlightning.accounting.valuation.failures", "{failure}",
                                                    "Back-valuation rounds that failed");
    }

    /// <summary>Unvalued postings per page (one save each; <see cref="DefaultPageSize"/>, smaller in tests).</summary>
    public int PageSize { get; init; } = DefaultPageSize;

    /// <summary>Whether the back-valuation runs: the books on and <c>Accounting:Prices</c> valid.</summary>
    public bool IsEnabled => _accountingOptions.AreBooksEnabled && _optionErrors.Count == 0;

    /// <inheritdoc />
    public string Currency => _priceOptions.NormalizedCurrency;

    /// <summary>The interval between rounds.</summary>
    public TimeSpan Interval => _priceOptions.FetchInterval > TimeSpan.Zero
                                    ? _priceOptions.FetchInterval
                                    : DefaultInterval;

    /// <summary>How long an hour the sources had no usable price for is left alone (at least an hour).</summary>
    public TimeSpan RetryDelay => Interval > TimeSpan.FromHours(1) ? Interval : TimeSpan.FromHours(1);

    /// <summary>Postings valued since the process started.</summary>
    public long TotalValued => Interlocked.Read(ref _totalValued);

    /// <summary>Prices asked of the sources since the process started.</summary>
    public long TotalFetched => Interlocked.Read(ref _totalFetched);

    /// <summary>The meter, for tests that assert the instruments.</summary>
    internal Meter Meter { get; }

    /// <summary>Starts the rounds (the first one at once). Nothing when <see cref="IsEnabled"/> is false (invalid options
    /// are logged); idempotent; nothing after <see cref="StopAsync"/>. The host starts it after the books.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started || _stopped || !_accountingOptions.AreBooksEnabled)
                return;

            if (_optionErrors.Count > 0)
            {
                _logger.LogError("The accounting back-valuation is off: {Errors}", string.Join("; ", _optionErrors));
                return;
            }

            _started = true;
            _loop = Task.Run(RunAsync);
        }
    }

    /// <summary>Asks for an early round.</summary>
    public void Nudge()
    {
        lock (_gate)
            _wake.TrySetResult();
    }

    /// <summary>Stops the rounds; a page in progress finishes first.</summary>
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

    /// <inheritdoc />
    public async Task<AccountingValuationRoundResult> ValueNowAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
            return AccountingValuationRoundResult.Empty;

        await _roundGate.WaitAsync(cancellationToken);
        try
        {
            return await RoundAsync(_priceOptions.MaxFetchesPerRound, true, cancellationToken);
        }
        finally
        {
            _roundGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<AccountingPriceImportResult> ImportAsync(string? currency,
                                                               IReadOnlyList<AccountingPricePoint> points,
                                                               CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(points);
        EnsureValidOptions();
        var code = NormalizeCurrency(currency);
        if (points.Count > MaxImportRows)
            throw new ArgumentException($"At most {MaxImportRows} prices per import ({points.Count} sent)",
                                        nameof(points));

        for (var i = 0; i < points.Count; i++)
        {
            if (points[i] is null)
                throw new ArgumentException($"Price {i + 1} is missing", nameof(points));
            if (!AccountingPriceCsv.TryValidate(points[i], out var error))
                throw new ArgumentException($"Price {i + 1}: {error}", nameof(points));
        }

        var added = 0;
        var now = _timeProvider.GetUtcNow();
        await _roundGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var batch in points.Chunk(ImportBatchSize))
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var batchAdded = 0;
                foreach (var point in batch)
                {
                    var price = new AccountingPrice(0, code, point.Time, RoundPrice(point.Price),
                                                    AccountingPriceSource.Import, now);
                    if (await unitOfWork.AccountingPriceDbRepository.TryAddAsync(price, cancellationToken))
                        batchAdded++;
                }

                if (batchAdded > 0)
                    await unitOfWork.SaveChangesAsync();
                added += batchAdded;
            }

            _storedCounter.Add(added);
            _fullPassDue = true;
            _logger.LogInformation("Imported {Added} {Currency} prices ({Kept} times already had one)", added, code,
                                   points.Count - added);

            var valuation = IsEnabled && code == Currency
                                ? await RoundAsync(0, true, cancellationToken)
                                : null;
            return new AccountingPriceImportResult(code, added, points.Count - added, valuation);
        }
        finally
        {
            _roundGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountingPrice>> ListAsync(string? currency, DateTimeOffset? since,
                                                                DateTimeOffset? until, int take,
                                                                CancellationToken cancellationToken = default)
    {
        EnsureValidOptions();
        var code = NormalizeCurrency(currency);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        return await unitOfWork.AccountingPriceDbRepository.ListAsync(code, since, until,
                                                                      Math.Clamp(take, 1, MaxListTake),
                                                                      cancellationToken);
    }

    /// <inheritdoc />
    public async Task<AccountingPriceFetchResult> FetchAsync(DateTimeOffset since, DateTimeOffset? until,
                                                             CancellationToken cancellationToken = default)
    {
        EnsureValidOptions();
        if (!_priceOptions.HasSource || _priceSource is null)
            throw new InvalidOperationException(
                $"{AccountingPriceOptions.SectionName}:Source is None: there is no price source to fetch from (import "
              + "prices instead)");

        var end = until ?? _timeProvider.GetUtcNow();
        var first = AccountingValuation.HourStart(since);
        if (end <= first)
            throw new ArgumentException("The range is empty: --until must be after --since", nameof(until));

        var hours = new List<DateTimeOffset>();
        for (var hour = first; hour < end; hour = hour.AddHours(1))
        {
            if (hours.Count == AccountingPriceOptions.MaxFetchHoursPerCommand)
                throw new ArgumentException(
                    $"At most {AccountingPriceOptions.MaxFetchHoursPerCommand} hours per fetch (31 days): narrow the "
                  + "range", nameof(since));
            hours.Add(hour);
        }

        await _roundGate.WaitAsync(cancellationToken);
        try
        {
            var currency = Currency;
            int requested = 0, stored = 0, unavailable = 0;
            await using (var scope = _scopeFactory.CreateAsyncScope())
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var prices = unitOfWork.AccountingPriceDbRepository;
                var covered = (await prices.ListAsync(currency, first, hours[^1].AddHours(1),
                                                      hours.Count * 60 + 1, cancellationToken))
                             .Select(p => AccountingValuation.HourStart(p.Time))
                             .ToHashSet();
                foreach (var hour in hours.Where(h => !covered.Contains(h)))
                {
                    requested++;
                    var price = await AskSourceAsync(currency, hour, cancellationToken);
                    if (price is null)
                    {
                        unavailable++;
                        continue;
                    }

                    if (await prices.TryAddAsync(price, cancellationToken))
                        stored++;
                }

                if (stored > 0)
                    await unitOfWork.SaveChangesAsync();

                _storedCounter.Add(stored);
                _fullPassDue = true;
                _logger.LogInformation(
                    "Fetched {Currency} prices for {Requested} of {Hours} hours from {Since:O}: {Stored} stored, "
                  + "{Unavailable} unavailable", currency, requested, hours.Count, first, stored, unavailable);
                var valuation = IsEnabled ? await RoundAsync(0, true, cancellationToken) : null;
                return new AccountingPriceFetchResult(currency, first, end, hours.Count, hours.Count - requested,
                                                      requested, stored, unavailable, valuation);
            }
        }
        finally
        {
            _roundGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stopping.Dispose();
        Meter.Dispose();
    }

    /// <summary>Cancels the loop without waiting for it (a container disposed synchronously); prefer
    /// <see cref="DisposeAsync"/>.</summary>
    public void Dispose()
    {
        lock (_gate)
            _stopped = true;

        try
        {
            _stopping.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Disposed asynchronously before
        }

        Meter.Dispose();
    }

    private async Task RunAsync()
    {
        var token = _stopping.Token;
        while (!token.IsCancellationRequested)
        {
            // A nudge that arrives during the round wakes the next wait at once
            Task wake;
            lock (_gate)
            {
                if (_wake.Task.IsCompleted)
                    _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wake = _wake.Task;
            }

            try
            {
                await _roundGate.WaitAsync(token);
                try
                {
                    await RoundAsync(_priceOptions.MaxFetchesPerRound, false, token);
                }
                finally
                {
                    _roundGate.Release();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // Only this round stops; the postings stay unvalued and the next round tries again
                _failureCounter.Add(1);
                _logger.LogError(e, "The accounting back-valuation round failed");
            }

            using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var delay = Task.Delay(Interval, _timeProvider, delayCancellation.Token);
            await Task.WhenAny(delay, wake);
            await delayCancellation.CancelAsync();
        }
    }

    /// <summary>One round; the caller holds the round gate.</summary>
    /// <param name="maxFetches">The most asks of the sources.</param>
    /// <param name="fullPass">Start at the beginning, also before the last closed period.</param>
    /// <param name="cancellationToken">Cancels between pages.</param>
    private async Task<AccountingValuationRoundResult> RoundAsync(int maxFetches, bool fullPass,
                                                                  CancellationToken cancellationToken)
    {
        var currency = Currency;
        var maxAge = _priceOptions.MaxAge;
        var now = _timeProvider.GetUtcNow();
        var closed = await GetClosedPeriodsAsync(cancellationToken);

        // A full pass when asked, at the first round, when the closed periods changed, and every FullPassInterval
        var signature = string.Join(',', closed.Select(p => $"{p.PeriodId}@{p.End.UtcTicks}"));
        if (fullPass || _fullPassDue || signature != _closedSignature || now >= _nextFullPass)
        {
            fullPass = true;
            _fullPassDue = false;
            _closedSignature = signature;
            _nextFullPass = now + FullPassInterval;
        }

        AccountingUnvaluedPostingCursor? cursor = null;
        if (!fullPass && closed.Count > 0)
            cursor = AccountingUnvaluedPostingCursor.StartOf(closed.Max(p => p.End));

        // With a source, a posting waits for the price of its own hour until that hour was asked; without one (or
        // with MaxFetchesPerRound 0) D-A11's nearest price within MaxAge values it at once
        var canFetch = _priceOptions.HasSource && _priceSource is not null && _priceOptions.MaxFetchesPerRound > 0;
        var fetchBudget = canFetch ? Math.Max(0, maxFetches) : 0;
        int listed = 0, valued = 0, fetched = 0, stored = 0, late = 0, closedLeft = 0, unpriced = 0, deferred = 0;

        for (var page = 0; page < MaxPagesPerRound; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var scope = _scopeFactory.CreateAsyncScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var books = unitOfWork.AccountingBooksDbRepository;
            var prices = unitOfWork.AccountingPriceDbRepository;

            var postings = await books.ListUnvaluedPostingsAsync(AccountingBook.Financial, cursor, PageSize,
                                                                 cancellationToken);
            if (postings.Count == 0)
                break;

            cursor = AccountingUnvaluedPostingCursor.After(postings[^1]);
            listed += postings.Count;

            var resolved = await ResolvePricesAsync(prices, currency, postings, maxAge, cancellationToken);

            // The hours still without their own price, each asked once (at its earliest posting's time)
            if (fetchBudget > 0 && postings.Any(WaitsForItsHour))
            {
                var missing = postings.Where(WaitsForItsHour)
                                      .GroupBy(p => AccountingValuation.HourStart(p.OccurredAt))
                                      .Select(g => (Hour: g.Key, At: g.Min(p => p.OccurredAt)))
                                      .OrderBy(h => h.Hour);
                var pageStored = 0;
                foreach (var (hour, at) in missing)
                {
                    if (fetchBudget == 0)
                        break;

                    fetchBudget--;
                    fetched++;
                    var price = await AskSourceAsync(currency, at, cancellationToken);
                    if (price is not null && price.Time >= hour && AccountingValuation.IsUsable(price.Time, at, maxAge))
                        _retryAfter.Remove(hour);
                    else
                        RememberAsked(hour, now);

                    if (price is not null && await prices.TryAddAsync(price, cancellationToken))
                        pageStored++;
                }

                if (pageStored > 0)
                {
                    // Saved first: a price's id exists only after its save
                    await unitOfWork.SaveChangesAsync();
                    stored += pageStored;
                    _storedCounter.Add(pageStored);
                    resolved = await ResolvePricesAsync(prices, currency, postings, maxAge, cancellationToken);
                }
            }

            foreach (var posting in postings)
            {
                if (resolved[posting.Key] is not { } price)
                {
                    unpriced++;
                    continue;
                }

                if (WaitsForItsHour(posting))
                {
                    deferred++;
                    continue;
                }

                var fiat = AccountingValuation.FiatValue(posting.AmountMsat, price.Price);
                if (IsClosed(posting, closed))
                {
                    // Never filled (D-A8): the adjustment rule carries the value into the open period
                    if (_adjusted.Contains(posting.Key))
                        continue;

                    if (await _adjustmentSink.AdjustLateValuationAsync(
                            unitOfWork, new AccountingLateValuation(posting, price, fiat), cancellationToken))
                    {
                        late++;
                        if (_adjusted.Count >= MaxRememberedAdjustments)
                            _adjusted.Clear();
                        _adjusted.Add(posting.Key);
                    }
                    else
                    {
                        closedLeft++;
                    }

                    continue;
                }

                if (await books.SetPostingValueAsync(posting.Key, fiat, currency, price.Id, cancellationToken))
                    valued++;
            }

            // Whether the posting still waits for a price of its own hour that may yet be asked
            bool WaitsForItsHour(AccountingUnvaluedPosting posting)
            {
                if (!canFetch)
                    return false;

                var hour = AccountingValuation.HourStart(posting.OccurredAt);
                if (resolved[posting.Key] is { } found && found.Time >= hour)
                    return false;

                // A recent hour's price may not be published yet: no older price for it before RecentHourWait
                var askedRecently = _retryAfter.TryGetValue(hour, out var retryAt) && now < retryAt;
                return !askedRecently || now < hour + RecentHourWait;
            }

            await unitOfWork.SaveChangesAsync();
            if (postings.Count < PageSize)
                break;
        }

        if (valued > 0)
        {
            _valuedCounter.Add(valued);
            Interlocked.Add(ref _totalValued, valued);
            _logger.LogDebug("Valued {Count} financial postings in {Currency}", valued, currency);
        }

        if (fetched > 0)
        {
            _fetchedCounter.Add(fetched);
            Interlocked.Add(ref _totalFetched, fetched);
        }

        if (late > 0)
        {
            _lateCounter.Add(late);
            _logger.LogInformation("Handed {Count} late valuations of closed periods to the adjustment rule", late);
        }

        if (closedLeft > 0 && !_reportedClosedLeft)
        {
            _reportedClosedLeft = true;
            _logger.LogInformation(
                "{Count} postings of closed periods have a price but no adjustment rule took them; they stay "
              + "unvalued (D-A8)", closedLeft);
        }

        return new AccountingValuationRoundResult(listed, valued, fetched, stored, late, closedLeft, unpriced)
        {
            Deferred = deferred
        };
    }

    /// <summary>The stored price of every posting (null where none is usable): one window query for the page, or one
    /// lookup per posting when the window holds more than <see cref="PriceWindowCap"/> prices.</summary>
    private static async Task<Dictionary<AccountingPostingKey, AccountingPrice?>> ResolvePricesAsync(
        IAccountingPriceDbRepository prices, string currency, IReadOnlyList<AccountingUnvaluedPosting> postings,
        TimeSpan maxAge, CancellationToken cancellationToken)
    {
        var earliest = postings.Min(p => p.OccurredAt);
        var latest = postings.Max(p => p.OccurredAt);
        DateTimeOffset? since = earliest - DateTimeOffset.MinValue > maxAge ? earliest - maxAge : null;
        var window = await prices.ListAsync(currency, since, latest.AddTicks(1), PriceWindowCap, cancellationToken);

        var resolved = new Dictionary<AccountingPostingKey, AccountingPrice?>(postings.Count);
        foreach (var posting in postings)
        {
            resolved[posting.Key] = window.Count < PriceWindowCap
                                        ? AccountingValuation.NearestAtOrBefore(window, p => p.Time,
                                                                                posting.OccurredAt, maxAge)
                                        : await prices.GetAtOrBeforeAsync(currency, posting.OccurredAt, maxAge,
                                                                          cancellationToken);
        }

        return resolved;
    }

    private async Task<AccountingPrice?> AskSourceAsync(string currency, DateTimeOffset at,
                                                        CancellationToken cancellationToken)
    {
        var price = await _priceSource!.GetPriceAsync(currency, at, cancellationToken);
        if (price is null || !AccountingPriceOptions.IsCurrencyCode(price.Currency?.Trim().ToUpperInvariant())
                          || price.Price <= 0 || price.Price > AccountingPriceCsv.MaxPrice)
            return null;

        return price with
        {
            Id = 0,
            Currency = currency,
            Price = RoundPrice(price.Price),
            FetchedAt = _timeProvider.GetUtcNow()
        };
    }

    private async Task<IReadOnlyList<AccountingPeriod>> GetClosedPeriodsAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        try
        {
            var periods = await unitOfWork.AccountingPeriodDbRepository.ListAsync(cancellationToken);
            return periods.Where(p => p.State == AccountingPeriodState.Closed).ToList();
        }
        catch (NotSupportedException)
        {
            // A unit of work without the periods' table (test doubles): nothing is closed
            return [];
        }
    }

    private static bool IsClosed(AccountingUnvaluedPosting posting, IReadOnlyList<AccountingPeriod> closed) =>
        posting.ClosedPeriodId is not null
     || closed.Any(p => p.Start <= posting.OccurredAt && posting.OccurredAt < p.End);

    private void RememberAsked(DateTimeOffset hour, DateTimeOffset now)
    {
        if (_retryAfter.Count >= MaxRememberedHours)
            _retryAfter.Clear();
        _retryAfter[hour] = now + RetryDelay;
    }

    private string NormalizeCurrency(string? currency)
    {
        if (currency is null)
            return Currency;

        var code = currency.Trim().ToUpperInvariant();
        return AccountingPriceOptions.IsCurrencyCode(code)
                   ? code
                   : throw new ArgumentException($"'{currency}' is not a three-letter ISO 4217 code",
                                                 nameof(currency));
    }

    private void EnsureValidOptions()
    {
        if (_optionErrors.Count > 0)
            throw new InvalidOperationException(string.Join("; ", _optionErrors));
    }

    private static decimal RoundPrice(decimal price) =>
        Math.Round(price, Domain.Accounting.Constants.AccountingSchemaLimits.FiatScale, MidpointRounding.ToEven);
}