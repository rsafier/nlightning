using System.Diagnostics.Metrics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Accounting.Prices;

using Domain.Accounting.Books;
using Domain.Accounting.Enums;
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
/// default sink takes nothing and the posting stays unvalued. Each page holds the sink's write lock
/// (<see cref="IAccountingAdjustmentSink.EnterAsync"/>) from its closed-period check to its save, never across the
/// source's asks, so a close never commits in between. To keep rounds cheap, a routine round starts after the
/// last closed period; a full pass from the beginning runs at the first round, every <see cref="FullPassInterval"/>,
/// when the closed periods change and after an import or a fetch.</para>
/// <para><b>The financial projector's replay</b> (A3-T4): when a page values a line of an entry the projector marked
/// <see cref="AccountingEntryFlags.PendingValuation"/> (projected before its price was known), the page lowers the
/// financial book's cursor to just before the earliest such entry in the same save; the projector then projects it
/// again with its lots, reliefs and realized gain.</para>
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
    private readonly Counter<long> _rejectedCounter;
    private readonly Counter<long> _replacedCounter;

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
    private volatile bool _catchingUp;
    private int _sourceFailures;
    private string? _lastSourceFailure;

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
        _rejectedCounter = Meter.CreateCounter<long>("nlightning.accounting.prices.rejected", "{price}",
                                                     "Fetched prices refused by the sanity bound (NL-678)");
        _replacedCounter = Meter.CreateCounter<long>("nlightning.accounting.prices.replaced", "{price}",
                                                     "Stored prices the operator replaced (NL-693)");
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

    /// <summary>
    /// Whether the last round used its whole fetch budget and still had hours to ask (NL-658): the back-valuation is
    /// catching up over old unpriced history, and the financial projector's background rounds wait with the replays it
    /// asks for (<see cref="Financial.FinancialBooksProjector.MaxReplayDeferral"/>).
    /// </summary>
    public bool IsCatchingUp => _catchingUp;

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
            ResetSourceFailures();
            await using (var scope = _scopeFactory.CreateAsyncScope())
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var prices = unitOfWork.AccountingPriceDbRepository;
                var covered = (await prices.ListAsync(currency, first, hours[^1].AddHours(1),
                                                      hours.Count * 60 + 1, cancellationToken))
                             .Select(p => AccountingValuation.HourStart(p.Time))
                             .ToHashSet();
                // The prices this batch accepted but has not saved yet: the sanity bound compares with them too (NL-733)
                var accepted = new List<AccountingPrice>();
                foreach (var hour in hours.Where(h => !covered.Contains(h)))
                {
                    requested++;
                    var price = await AskSourceAsync(currency, hour, cancellationToken);
                    if (price is not null && !await IsPlausibleAsync(prices, price, accepted, cancellationToken))
                        price = null;
                    if (price is null)
                    {
                        unavailable++;
                        continue;
                    }

                    if (await prices.TryAddAsync(price, cancellationToken))
                    {
                        stored++;
                        accepted.Add(price);
                    }
                }

                if (stored > 0)
                    await unitOfWork.SaveChangesAsync();

                _storedCounter.Add(stored);
                _fullPassDue = true;
                _logger.LogInformation(
                    "Fetched {Currency} prices for {Requested} of {Hours} hours from {Since:O}: {Stored} stored, "
                  + "{Unavailable} unavailable", currency, requested, hours.Count, first, stored, unavailable);
                ReportSourceFailures(currency, requested);
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

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountingPriceReplacementAudit>> ListReplacementAuditsAsync(
        IReadOnlyCollection<long> priceIds, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var prices = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingPriceDbRepository;
        var result = new List<AccountingPriceReplacementAudit>();
        foreach (var id in priceIds.Order())
            result.AddRange(await prices.ListReplacementAuditsAsync(id, cancellationToken));
        return result.OrderBy(a => a.ReplacedAt).ThenBy(a => a.Id).ToList();
    }

    /// <inheritdoc />
    public async Task<AccountingPriceReplaceResult> ReplaceAsync(AccountingPriceReplacement replacement,
                                                                 CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        EnsureValidOptions();
        var code = NormalizeCurrency(replacement.Currency);
        if (!AccountingPriceCsv.TryValidate(new AccountingPricePoint(replacement.Time, replacement.Price), out var error))
            throw new ArgumentException($"The new price: {error}", nameof(replacement));

        var source = AuditText(replacement.Source, AccountingPriceReplacement.MaxSourceLength, "--source");
        var reason = AuditText(replacement.Note, AccountingPriceReplacement.MaxNoteLength, "--note");
        var newPrice = RoundPrice(replacement.Price);

        await _roundGate.WaitAsync(cancellationToken);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var prices = unitOfWork.AccountingPriceDbRepository;

            // The stored price of that second (prices list shows Unix seconds)
            var second = new DateTimeOffset(replacement.Time.UtcTicks - replacement.Time.UtcTicks % TimeSpan.TicksPerSecond,
                                            TimeSpan.Zero);
            var matches = await prices.ListAsync(code, second, second.AddSeconds(1), 2, cancellationToken);
            var stored = matches.Count switch
            {
                0 => throw new ArgumentException(
                         $"No {code} price is stored at {second:O}: 'accounting prices list' shows the stored times, "
                       + "'accounting prices import' adds a price", nameof(replacement)),
                1 => matches[0],
                _ => throw new ArgumentException($"More than one {code} price is stored in the second {second:O}",
                                                 nameof(replacement))
            };

            if (stored.Price == newPrice)
                return new AccountingPriceReplaceResult(stored, stored.Price, stored.Source, stored.FetchedAt, false,
                                                        null, 0, 0, 0, 0);

            // The period lock's write lock from the closed-period checks to the save (A3-T5)
            using var writeLock = await _adjustmentSink.EnterAsync(cancellationToken);
            var now = _timeProvider.GetUtcNow();
            if (!await prices.ReplaceAsync(stored.Id, newPrice, AccountingPriceSource.Manual, now, cancellationToken))
                throw new ArgumentException($"The {code} price at {second:O} is no longer stored", nameof(replacement));

            prices.AddReplacementAudit(new AccountingPriceReplacementAudit(0, stored.Id, stored.Price, newPrice,
                stored.Source, AccountingPriceSource.Manual, stored.FetchedAt, now, source, reason));

            var audit = $"{code} price of {stored.Time.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)} "
                      + $"replaced: {Format(stored.Price)} ({stored.Source}) -> {Format(newPrice)}"
                      + (source is null ? string.Empty : $", source {source}")
                      + (reason is null ? string.Empty : $": {reason}");
            var revaluation = await RepriceAsync(unitOfWork, stored, newPrice, now, audit, cancellationToken);
            if (revaluation.ReplayFrom is { } from)
            {
                // The projector values the open period again from just before it (the cursor commits with the price)
                var books = unitOfWork.AccountingBooksDbRepository;
                if (from - 1 < await books.GetCursorAsync(AccountingBook.Financial, cancellationToken))
                    await books.SetCursorAsync(AccountingBook.Financial, from - 1, cancellationToken);
            }

            await unitOfWork.SaveChangesAsync();
            _replacedCounter.Add(1);
            _logger.LogWarning(
                "Accounting: the operator replaced the {Audit} (price id {PriceId}); {Open} open entries projected "
              + "again from ledger sequence {ReplayFrom}, {Adjustments} price adjustments for {Closed} entries of "
              + "closed periods", audit, stored.Id, revaluation.OpenEntries, revaluation.ReplayFrom,
                revaluation.Adjustments, revaluation.ClosedEntries);

            return new AccountingPriceReplaceResult(
                stored with { Price = newPrice, Source = AccountingPriceSource.Manual, FetchedAt = now }, stored.Price,
                stored.Source, stored.FetchedAt, true, revaluation.ReplayFrom, revaluation.OpenEntries,
                revaluation.ClosedEntries, revaluation.Adjustments, revaluation.LinesRepriced);
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
        var budgetLeftOver = false;
        ResetSourceFailures();

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

            // A late fact's lines (NL-671) are valued at the fact's time, never the adjustment's: they stand at the
            // fact's time here; a price found for an open one replays the fact (the projector stages it again), and one
            // closed unvalued by a forced close gets its price adjustment at the fact's price too (NL-680)
            var lateFacts = await LateFactTimesAsync(books, postings, cancellationToken);
            var own = postings.ToDictionary(p => p.Key);
            if (lateFacts.Count > 0)
                postings = postings.Select(p => lateFacts.TryGetValue(p.Key, out var factAt)
                                                    ? p with { OccurredAt = factAt }
                                                    : p).ToList();

            var resolved = await ResolvePricesAsync(prices, currency, postings, maxAge, cancellationToken);

            // The hours still without their own price, each asked once (at its earliest posting's time)
            // A page that still waits for hours after the budget ran out (also exactly on the previous page) is a
            // catch-up too (NL-734); a round that may not fetch at all (an import's or fetch's valuation) is not
            if (fetchBudget == 0 && maxFetches > 0 && canFetch && postings.Any(WaitsForItsHour))
                budgetLeftOver = true;

            if (fetchBudget > 0 && postings.Any(WaitsForItsHour))
            {
                var missing = postings.Where(WaitsForItsHour)
                                      .GroupBy(p => AccountingValuation.HourStart(p.OccurredAt))
                                      .Select(g => (Hour: g.Key, At: g.Min(p => p.OccurredAt)))
                                      .OrderBy(h => h.Hour);
                var pageStored = 0;
                var accepted = new List<AccountingPrice>(); // staged, not saved yet: checked against too (NL-733)
                foreach (var (hour, at) in missing)
                {
                    if (fetchBudget == 0)
                    {
                        // More hours to ask than this round may: a catch-up over old history
                        budgetLeftOver = true;
                        break;
                    }

                    fetchBudget--;
                    fetched++;
                    var price = await AskSourceAsync(currency, at, cancellationToken);
                    if (price is not null && !await IsPlausibleAsync(prices, price, accepted, cancellationToken))
                        price = null;
                    if (price is not null && price.Time >= hour && AccountingValuation.IsUsable(price.Time, at, maxAge))
                        _retryAfter.Remove(hour);
                    else
                        RememberAsked(hour, now);

                    if (price is not null && await prices.TryAddAsync(price, cancellationToken))
                    {
                        pageStored++;
                        accepted.Add(price);
                    }
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

            // The period lock's write lock from the closed-period check to the save (A3-T5), never across the asks
            using var writeLock = await _adjustmentSink.EnterAsync(cancellationToken);
            var closedNow = await GetClosedPeriodsAsync(unitOfWork, cancellationToken);
            long? replayFrom = null;
            var adjustedOnPage = new List<AccountingPostingKey>();
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

                var ownPosting = own[posting.Key];
                var closedNowToo = IsClosed(ownPosting, closedNow);
                if (lateFacts.ContainsKey(posting.Key) && !closedNowToo)
                {
                    // Never filled in place: the projector stages the late fact again at this price
                    if (replayFrom is null || posting.Key.LedgerSeq < replayFrom)
                        replayFrom = posting.Key.LedgerSeq;
                    continue;
                }

                var fiat = AccountingValuation.FiatValue(posting.AmountMsat, price.Price);
                if (closedNowToo)
                {
                    // Never filled (D-A8): the adjustment rule carries the value into the open period
                    if (_adjusted.Contains(posting.Key))
                        continue;

                    if (await _adjustmentSink.AdjustLateValuationAsync(
                            unitOfWork, new AccountingLateValuation(ownPosting, price, fiat), cancellationToken))
                    {
                        late++;
                        adjustedOnPage.Add(posting.Key);
                    }
                    else
                    {
                        closedLeft++;
                    }

                    continue;
                }

                if (await books.SetPostingValueAsync(posting.Key, fiat, currency, price.Id, cancellationToken))
                {
                    valued++;
                    if (posting.Key is { Book: AccountingBook.Financial, Adjustment: 0 }
                     && posting.EntryFlags.HasFlag(AccountingEntryFlags.PendingValuation)
                     && (replayFrom is null || posting.Key.LedgerSeq < replayFrom))
                        replayFrom = posting.Key.LedgerSeq;
                }
            }

            // An entry the financial projector projected before its price was known (A3-T4): the projector values it
            // again, with its lots and gains, from just before it (the cursor commits with the values)
            if (replayFrom is { } from)
            {
                var financialCursor = await books.GetCursorAsync(AccountingBook.Financial, cancellationToken);
                if (from - 1 < financialCursor)
                    await books.SetCursorAsync(AccountingBook.Financial, from - 1, cancellationToken);
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
            // Remember only durable adjustments: a failed save must leave the next round free to retry them.
            foreach (var key in adjustedOnPage)
            {
                if (_adjusted.Count >= MaxRememberedAdjustments)
                    _adjusted.Clear();
                _adjusted.Add(key);
            }
            if (postings.Count < PageSize)
                break;
        }

        _catchingUp = budgetLeftOver;
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
            ReportSourceFailures(currency, fetched);
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

    /// <summary>
    /// Re-values what the replaced price <paramref name="stored"/> priced in the financial book (NL-693), staged on
    /// <paramref name="unitOfWork"/> (the caller holds the period lock's write lock and saves). Every entry dated at or
    /// after the price's time with a line valued with it (no other can use it: a price values what follows it):
    /// <list type="bullet">
    /// <item>an open entry the projector projected (adjustment 0 or a late fact): projected again, from the earliest
    /// (the projector rolls the open period back to it: values, lots, reliefs and gains as a rebuild would have
    /// them);</item>
    /// <item>an entry of a closed period, or an adjustment of one in the open period: a <c>Price</c> adjustment in the
    /// open period with the change of its lines' values (<see cref="AccountingRepricing"/>), once per entry and
    /// replacement; a fact a reorg reversed after its close is left alone when its reversal is closed too (the pair
    /// nets to zero), and its open reversal is projected again, so it takes the correction back with the fact (the
    /// projector negates a closed fact's corrections in its reversal).</item>
    /// </list>
    /// </summary>
    private async Task<Revaluation> RepriceAsync(IUnitOfWork unitOfWork, AccountingPrice stored, decimal newPrice,
                                                 DateTimeOffset now, string audit,
                                                 CancellationToken cancellationToken)
    {
        const int pageSize = 500;
        var books = unitOfWork.AccountingBooksDbRepository;
        var chart = _accountingOptions.GetFinancialChart();
        long? replayFrom = null;
        int open = 0, closed = 0, adjustments = 0, repriced = 0;
        var factTimes = new Dictionary<long, DateTimeOffset?>();

        // The reversals of closed facts (a reorg after the close): a closed one already took the fact's value back, so
        // the fact is left as it is; an open one is projected again after the correction, so it takes the corrected
        // value back (the projector negates the fact's corrections with it). A reversal of a closed period itself is a
        // late fact, an adjustment of the open period the projector stages again when it replays from its sequence
        // (NL-766), and is treated the same: closed when that period is closed too
        var reversals = new Dictionary<string, AccountingEntry>(StringComparer.Ordinal);
        long afterSeq = 0;
        var afterAdjustment = -1;
        while (true)
        {
            var page = await books.ListEntriesAsync(new AccountingEntryQuery(afterSeq, pageSize, stored.Time,
                                                                             Kinds: [AccountingEventKind.Reversal])
            {
                Book = AccountingBook.Financial,
                AfterAdjustment = afterAdjustment
            }, cancellationToken);
            foreach (var reversal in page.Where(r => r.Adjustment == 0
                                                  || r.Flags.HasFlag(AccountingEntryFlags.LateFact)))
            {
                const string prefix = "reverses ";
                var at = reversal.Note?.IndexOf(prefix, StringComparison.Ordinal) ?? -1;
                var end = at < 0 ? -1 : reversal.Note!.IndexOf(" of a closed period", at, StringComparison.Ordinal);
                if (end > at)
                    reversals[reversal.Note![(at + prefix.Length)..end]] = reversal;
            }

            if (page.Count < pageSize)
                break;
            afterSeq = page[^1].LedgerSeq;
            afterAdjustment = page[^1].Adjustment;
        }

        afterSeq = 0;
        afterAdjustment = -1;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await books.ListEntriesAsync(new AccountingEntryQuery(afterSeq, pageSize, stored.Time)
            {
                Book = AccountingBook.Financial,
                AfterAdjustment = afterAdjustment
            }, cancellationToken);
            foreach (var entry in page)
            {
                if (IsRepricing(entry) || !entry.Postings.Any(p => p.PriceId == stored.Id))
                    continue;

                if (entry.ClosedPeriodId is null
                 && (entry.Adjustment == 0 || entry.Flags.HasFlag(AccountingEntryFlags.LateFact)))
                {
                    open++;
                    replayFrom = Math.Min(replayFrom ?? long.MaxValue, entry.LedgerSeq);
                    continue;
                }

                closed++;
                reversals.TryGetValue(entry.EventKey, out var reversedBy);
                if (reversedBy is { ClosedPeriodId: not null })
                    continue;

                var lines = await RepricedLinesAsync(books, entry, stored.Id, cancellationToken);
                var corrections = AccountingRepricing.Corrections(lines, entry.Postings, stored.Id, stored.Price,
                                                                  newPrice, stored.Currency, chart);
                if (corrections.Count == 0)
                    continue;

                if (!factTimes.TryGetValue(entry.LedgerSeq, out var factAt))
                {
                    // The fact's time: its operational entry's
                    var operational = await books.ListEntriesAsync(new AccountingEntryQuery(entry.LedgerSeq - 1, 1),
                                                                   cancellationToken);
                    factAt = operational.Count == 1 && operational[0].LedgerSeq == entry.LedgerSeq
                                 ? operational[0].OccurredAt
                                 : null;
                    factTimes[entry.LedgerSeq] = factAt;
                }

                try
                {
                    var staged = await _adjustmentSink.StageAdjustmentAsync(
                                     unitOfWork,
                                     new AccountingAdjustment(AccountingAdjustmentReason.Price, entry.LedgerSeq,
                                                              entry.EventKey, entry.Kind, factAt ?? entry.OccurredAt,
                                                              corrections)
                                     {
                                         ChannelId = entry.ChannelId,
                                         PaymentHash = entry.PaymentHash,
                                         // One per entry and replacement (the time and the price replaced)
                                         DedupeKey = $"reprice:{stored.Id}:{now.UtcTicks}:{Format(stored.Price)}:"
                                                   + $"{entry.LedgerSeq}:"
                                                   + entry.Adjustment.ToString(CultureInfo.InvariantCulture),
                                         Note = audit
                                     }, cancellationToken);
                    if (staged is null)
                        continue;

                    adjustments++;
                    repriced += corrections.Count(c => c.PriceId == stored.Id);
                    if (reversedBy is not null)
                        replayFrom = Math.Min(replayFrom ?? long.MaxValue, reversedBy.LedgerSeq);
                }
                catch (ArgumentException e)
                {
                    // A fact the lock does not hold (its period is open after all): nothing to adjust, logged
                    _logger.LogWarning(e, "Accounting: the price adjustment of {EventKey} for the replaced price could "
                                        + "not be staged", entry.EventKey);
                }
            }

            if (page.Count < pageSize)
                break;

            afterSeq = page[^1].LedgerSeq;
            afterAdjustment = page[^1].Adjustment;
        }

        return new Revaluation(replayFrom, open, closed, adjustments, repriced);
    }

    /// <summary>The lines of <paramref name="entry"/> valued with the price <paramref name="priceId"/>, with the msat
    /// their value is of: their own, or for a zero-msat <c>Price</c> adjustment line (a late valuation, tagged
    /// <c>[price:{seq}:{adjustment}:{index}]</c>) the posting it valued.</summary>
    private static async Task<IReadOnlyList<RepricedLine>> RepricedLinesAsync(IAccountingBooksDbRepository books,
                                                                             AccountingEntry entry, long priceId,
                                                                             CancellationToken cancellationToken)
    {
        var lines = new List<RepricedLine>();
        IReadOnlyList<AccountingEntry>? siblings = null;
        foreach (var line in entry.Postings.Where(p => p.PriceId == priceId))
        {
            if (line.AmountMsat != 0)
            {
                lines.Add(new RepricedLine(line, line.AmountMsat, line.Account));
                continue;
            }

            if (LateValuationOf(entry.Note) is not { } valued)
                continue;

            siblings ??= await books.GetEntriesByKeyAsync(AccountingBook.Financial, entry.EventKey, cancellationToken);
            if (siblings.FirstOrDefault(e => e.Adjustment == valued.Adjustment)?.Postings.ElementAtOrDefault(valued.Index)
                    is { AmountMsat: not 0 } posting)
                lines.Add(new RepricedLine(line, posting.AmountMsat, posting.Account));
        }

        return lines;
    }

    /// <summary>The posting a late valuation's adjustment valued, from its note's tag
    /// (<c>[price:{seq}:{adjustment}:{index}] ...</c>, <c>AccountingPeriodService.AdjustLateValuationAsync</c>).</summary>
    private static (int Adjustment, int Index)? LateValuationOf(string? note)
    {
        if (note is null || !note.StartsWith("[price:", StringComparison.Ordinal))
            return null;

        var end = note.IndexOf(']', StringComparison.Ordinal);
        var parts = end > 0 ? note[1..end].Split(':') : [];
        return parts.Length == 4
            && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var adjustment)
            && int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                   ? (adjustment, index)
                   : null;
    }

    private static string? AuditText(string? text, int maxLength, string what)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();
        if (trimmed.Length > maxLength || trimmed.Any(char.IsControl))
            throw new ArgumentException($"{what} must be at most {maxLength} printable characters", nameof(text));

        return trimmed;
    }

    /// <summary>The note tag of a price replacement's correction (<c>[reprice:{priceId}:...]</c>).</summary>
    internal const string RepricingTag = "[reprice:";

    /// <summary>Whether <paramref name="entry"/> is a price replacement's correction (NL-693).</summary>
    internal static bool IsRepricing(AccountingEntry entry) =>
        entry.Adjustment > 0 && entry.Note?.StartsWith(RepricingTag, StringComparison.Ordinal) == true;

    private static string Format(decimal price) => price.ToString("0.########", CultureInfo.InvariantCulture);

    /// <summary>What a price replacement re-valued.</summary>
    private sealed record Revaluation(long? ReplayFrom, int OpenEntries, int ClosedEntries, int Adjustments,
                                      int LinesRepriced);

    /// <summary>
    /// The fact's time of every posting of a late fact waiting for its price (an adjustment flagged
    /// <see cref="AccountingEntryFlags.LateFact"/> and <see cref="AccountingEntryFlags.PendingValuation"/>, NL-671; open,
    /// or closed by a forced close, NL-680): the time of the operational entry it projects. Postings whose operational
    /// entry cannot be found are left out.
    /// </summary>
    private static async Task<Dictionary<AccountingPostingKey, DateTimeOffset>> LateFactTimesAsync(
        IAccountingBooksDbRepository books, IReadOnlyList<AccountingUnvaluedPosting> postings,
        CancellationToken cancellationToken)
    {
        const AccountingEntryFlags pendingLateFact = AccountingEntryFlags.LateFact
                                                   | AccountingEntryFlags.PendingValuation;
        var result = new Dictionary<AccountingPostingKey, DateTimeOffset>();
        var factTimes = new Dictionary<long, DateTimeOffset?>();
        foreach (var posting in postings)
        {
            if (posting.Key is not { Book: AccountingBook.Financial, Adjustment: > 0 } key
             || (posting.EntryFlags & pendingLateFact) != pendingLateFact)
                continue;

            if (!factTimes.TryGetValue(key.LedgerSeq, out var factAt))
            {
                var operational = await books.ListEntriesAsync(new AccountingEntryQuery(key.LedgerSeq - 1, 1),
                                                               cancellationToken);
                factAt = operational.Count == 1 && operational[0].LedgerSeq == key.LedgerSeq
                             ? operational[0].OccurredAt
                             : null;
                factTimes[key.LedgerSeq] = factAt;
            }

            if (factAt is { } at)
                result[key] = at;
        }

        return result;
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

    private void ResetSourceFailures()
    {
        _sourceFailures = 0;
        _lastSourceFailure = null;
    }

    /// <summary>
    /// One warning for the source's failures of a round or <c>prices fetch</c> (NL-868): the HTTP source logs each at
    /// Debug, so an unreachable source no longer warns once per hour asked.
    /// </summary>
    private void ReportSourceFailures(string currency, int asked)
    {
        if (_sourceFailures == 0)
            return;

        _logger.LogWarning("The price source failed for {Failed} of {Asked} {Currency} hours asked this round; the "
                         + "postings stay unvalued until a later round gets their price. Last failure: {Failure}",
                           _sourceFailures, asked, currency, _lastSourceFailure);
    }

    private async Task<AccountingPrice?> AskSourceAsync(string currency, DateTimeOffset at,
                                                        CancellationToken cancellationToken)
    {
        var price = await _priceSource!.GetPriceAsync(currency, at, cancellationToken);
        if (_priceSource.LastFailure is { } failure)
        {
            _sourceFailures++;
            _lastSourceFailure = failure;
        }

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

    /// <summary>
    /// The sanity bound of a fetched price (NL-678): a price from the HTTP source that differs from the nearest saved
    /// price within <see cref="AccountingPriceOptions.MaxAge"/> before or after its time by more than
    /// <see cref="AccountingPriceOptions.MaxPriceJumpFactor"/> is refused (logged, counted, never stored), so a stored
    /// price, which is never replaced, cannot come from a source's decimal-point or unit mistake next to good ones.
    /// Imported and file prices are the operator's and are not checked; nor is a price without a saved neighbor or one
    /// in <paramref name="batch"/>: the prices the same fetch or page accepted and staged but has not saved yet, which
    /// count as neighbors too (NL-733), so a 10x price fetched between good ones of one batch is refused as well.
    /// </summary>
    private async Task<bool> IsPlausibleAsync(IAccountingPriceDbRepository prices, AccountingPrice price,
                                              IReadOnlyList<AccountingPrice> batch,
                                              CancellationToken cancellationToken)
    {
        if (price.Source != AccountingPriceSource.Http || _priceOptions.MaxPriceJumpFactor == 0)
            return true;

        var maxAge = _priceOptions.MaxAge;
        var before = await prices.GetAtOrBeforeAsync(price.Currency, price.Time, maxAge, cancellationToken);
        foreach (var staged in batch)
        {
            if (staged.Currency == price.Currency && staged.Time <= price.Time && price.Time - staged.Time <= maxAge
             && (before is null || staged.Time > before.Time))
                before = staged;
        }

        var neighbor = before;
        if (before is null || _priceOptions.IsPlausibleNext(price.Price, before.Price))
        {
            var until = DateTimeOffset.MaxValue - price.Time > maxAge ? price.Time + maxAge : DateTimeOffset.MaxValue;
            var after = await prices.ListAsync(price.Currency, price.Time.AddTicks(1), until, 1, cancellationToken);
            neighbor = after.Count > 0 ? after[0] : null;
            foreach (var staged in batch)
            {
                if (staged.Currency == price.Currency && staged.Time > price.Time && staged.Time <= until
                 && (neighbor is null || staged.Time < neighbor.Time))
                    neighbor = staged;
            }

            if (neighbor is null || _priceOptions.IsPlausibleNext(price.Price, neighbor.Price))
                return true;
        }

        _rejectedCounter.Add(1);
        _logger.LogWarning(
            "Refused the price source's {Currency} price {Price} at {Time:O}: it is more than {Factor} times away from "
          + "the stored {Neighbor} at {NeighborTime:O} ({Section}:MaxPriceJumpFactor); import the right price with "
          + "'accounting prices import' if the source is right", price.Currency, price.Price, price.Time,
            _priceOptions.MaxPriceJumpFactor, neighbor!.Price, neighbor.Time, AccountingPriceOptions.SectionName);
        return false;
    }

    private async Task<IReadOnlyList<AccountingPeriod>> GetClosedPeriodsAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await GetClosedPeriodsAsync(scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
                                           cancellationToken);
    }

    private static async Task<IReadOnlyList<AccountingPeriod>> GetClosedPeriodsAsync(
        IUnitOfWork unitOfWork, CancellationToken cancellationToken)
    {
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