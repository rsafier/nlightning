using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Accounting.Financial;

using Domain.Accounting.Books;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Financial;
using Domain.Accounting.Financial.Classification;
using Domain.Accounting.Financial.Lots;
using Domain.Accounting.Financial.Reports;
using Domain.Accounting.Models;
using Domain.Accounting.Prices;
using Domain.Accounting.Services;
using Domain.Persistence.Interfaces;
using Prices;

/// <summary>
/// The financial book's projector (NL-602 A3-T4, plan <c>docs/agents/ACCOUNTING_PLAN.md</c> §6.2, D-A7, D-A9, D-A12):
/// it reads the operational entries in ledger order after its own cursor (never the raw feed), classifies them
/// (<see cref="ClassificationEngine"/>, A3-T3), values them at the stored prices (A3-T2, D-A11), keeps the cost-basis
/// lots (<see cref="FinancialEntryPlanner"/>) and writes the financial entries, lots, reliefs and its cursor in one save
/// per page. It is the <see cref="IFinancialBooksProjector"/> of the period close and the financial reports, and the lot
/// import (<see cref="IAccountingLots"/>).
/// </summary>
/// <remarks>
/// <para><b>Valuation.</b> An entry takes the stored price nearest at or before its time within
/// <c>Accounting:Prices:MaxAge</c>. With a price source that can fetch, as the back-valuation does, a price older than
/// the entry's own UTC hour is used only once the hour is <see cref="PriceValuationService.RecentHourWait"/> old. Without
/// a usable price the entry is projected unvalued (<see cref="AccountingEntryFlags.PendingValuation"/>): its lots and
/// reliefs carry no fiat. The back-valuation then values its lines and lowers this book's cursor in the same save (to
/// just before the entry); the next round sees open entries above the cursor, rolls the book back to the cursor
/// (<see cref="IAccountingBooksDbRepository.RollbackOpenEntriesAsync"/>: entries, lots and reliefs) and projects them
/// again at the stored price, so lots, reliefs and gains are those a rebuild would write.</para>
/// <para><b>Reversals</b> (reorgs): a fact and its reversal both after the cursor are a cancelled pair: the fact is
/// valued but touches no lot, and the reversal's entry is its exact negation. A reversal of a fact the book already
/// projected in the open period rolls the book back to that fact and projects again from there (the open period's lots
/// rebuilt from that point). A reversal of a closed period's fact posts the negation of the fact's financial lines, at
/// their values, in the open period (flagged <see cref="AccountingEntryFlags.Adjustment"/>): the lots follow the
/// usual rules (a reversed acquisition disposes, a reversed disposal acquires).</para>
/// <para><b>The period lock</b> (A3-T5): each page holds <see cref="IAccountingAdjustmentSink.EnterAsync"/> from its
/// checks to its save; an operational entry dated in a locked period goes to
/// <see cref="IAccountingAdjustmentSink.StageAdjustmentAsync"/> as a <see cref="AccountingAdjustmentReason.LateFact"/>
/// (valued at any usable price, never replayed), its lots and reliefs dated as the adjustment.</para>
/// <para><b>Opening balances</b> (D-A9) open lots at the cutover price, <c>BasisEstimated</c>. After a lot import
/// (<see cref="ImportAsync"/>) they open none: the imported lots replace them, and the cutover marker's entry moves the
/// assets' fiat from the opening balances' market value to the imported cost (<c>assets:cost-basis</c> against
/// <c>equity:opening-balances</c>).</para>
/// <para>A failure at an entry (a bug) stops the round before that page is saved; the next round tries again (it never
/// skips an entry). An exception stops only the financial book.</para>
/// </remarks>
public sealed class FinancialBooksProjector : IFinancialBooksProjector, IAccountingLots, IAsyncDisposable, IDisposable
{
    /// <summary>The most pages one round of the background loop projects.</summary>
    public const int MaxBatchesPerRound = 100;

    /// <summary>The most rollbacks one call does before it gives up for the round (a guard against a loop).</summary>
    public const int MaxRollbacksPerCall = 20;

    private readonly Lock _stateGate = new();
    private readonly SemaphoreSlim _roundGate = new(1, 1);
    private readonly ILogger<FinancialBooksProjector> _logger;
    private readonly AccountingOptions _options;
    private readonly AccountingPriceOptions _priceOptions;
    private readonly Func<IAccountingAdjustmentSink?> _sinkFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _stopping = new();
    private readonly FinancialChart _chart;
    private readonly bool _canFetch;
    private readonly string? _currencyError;
    private readonly Counter<long> _projectedCounter;
    private readonly Counter<long> _rollbackCounter;
    private readonly Counter<long> _failureCounter;
    private readonly Counter<long> _shortfallCounter;

    private Task? _loop;
    private bool _started;
    private bool _stopped;
    private string? _reportedFailureKey;
    private volatile string? _projectionError;

    /// <param name="scopeFactory">The scopes of the pages.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="options">The books' options (<c>Profile</c>, <c>CostBasis</c>, the chart).</param>
    /// <param name="priceOptions">The prices' options (currency, maximum age, source).</param>
    /// <param name="adjustmentSink">The period lock (A3-T5), resolved at the first round (it depends on this
    /// projector); none = <see cref="NullAccountingAdjustmentSink"/>.</param>
    /// <param name="hasPriceSource">Whether a price source is registered (with a fetching source configured, an entry
    /// waits for the price of its own hour as the back-valuation does).</param>
    /// <param name="timeProvider">The clock.</param>
    public FinancialBooksProjector(IServiceScopeFactory scopeFactory, ILogger<FinancialBooksProjector> logger,
                                   IOptions<AccountingOptions>? options = null,
                                   IOptions<AccountingPriceOptions>? priceOptions = null,
                                   Func<IAccountingAdjustmentSink?>? adjustmentSink = null, bool hasPriceSource = false,
                                   TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options?.Value ?? new AccountingOptions();
        _priceOptions = priceOptions?.Value ?? new AccountingPriceOptions();
        _sinkFactory = adjustmentSink ?? (() => null);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _chart = _options.GetFinancialChart();
        _canFetch = hasPriceSource && _priceOptions.HasSource && _priceOptions.MaxFetchesPerRound > 0;
        try
        {
            Currency = AccountingFiat.NormalizeCurrency(_priceOptions.Currency);
        }
        catch (ArgumentException e)
        {
            Currency = AccountingFiat.DefaultCurrency;
            _currencyError = e.Message;
        }

        Meter = new Meter(AccountingEventSealerService.MeterName);
        _projectedCounter = Meter.CreateCounter<long>("nlightning.accounting.financial.projected", "{entry}",
                                                      "Operational entries projected into the financial book");
        _rollbackCounter = Meter.CreateCounter<long>("nlightning.accounting.financial.rollbacks", "{rollback}",
                                                     "Rollbacks of the financial book's open entries to project them "
                                                   + "again (a price found late, a reversal, a reclassification)");
        _failureCounter = Meter.CreateCounter<long>("nlightning.accounting.financial.failures", "{failure}",
                                                    "Operational entries the financial book could not project (a "
                                                  + "bug); the projection waits at the entry");
        _shortfallCounter = Meter.CreateCounter<long>("nlightning.accounting.lots.shortfall", "msat",
                                                      "Msat disposed of that no cost-basis lot covered");
    }

    /// <inheritdoc />
    public bool IsEnabled => _options.IsFinancialBookEnabled && _currencyError is null;

    /// <summary>The book's currency (<c>Accounting:Prices:Currency</c>).</summary>
    public string Currency { get; }

    /// <summary>The cost-basis method in effect.</summary>
    public AccountingCostBasisMethod Method => _options.CostBasis;

    /// <summary>The interval between rounds (the sealer's).</summary>
    public TimeSpan Interval => _options.SealInterval > TimeSpan.Zero
                                    ? _options.SealInterval
                                    : AccountingOptions.DefaultSealInterval;

    /// <summary>The entry the projection waits at (its key and the error), or null.</summary>
    public string? ProjectionError => _projectionError;

    /// <summary>The meter, for tests that assert the instruments.</summary>
    internal Meter Meter { get; }

    private IAccountingAdjustmentSink Sink => _sinkFactory() ?? NullAccountingAdjustmentSink.Instance;

    /// <summary>Starts the rounds (the first one at once). Nothing when the financial book is off (an invalid currency
    /// is logged); idempotent; nothing after <see cref="StopAsync"/>. The host starts it after the operational books
    /// and stops it before them.</summary>
    public void Start()
    {
        lock (_stateGate)
        {
            if (_started || _stopped || !_options.IsFinancialBookEnabled)
                return;

            if (_currencyError is not null)
            {
                _logger.LogError("The financial book is off: Accounting:Prices:Currency: {Error}", _currencyError);
                return;
            }

            _started = true;
            _loop = Task.Run(RunAsync);
        }
    }

    /// <summary>Stops the rounds; a page in progress finishes first.</summary>
    public async Task StopAsync()
    {
        Task? loop;
        lock (_stateGate)
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
    public async Task<int> ProjectAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
            return 0;

        await _roundGate.WaitAsync(cancellationToken);
        try
        {
            return await ProjectCoreAsync(int.MaxValue, cancellationToken);
        }
        finally
        {
            _roundGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<T> RunExclusiveAsync<T>(Func<CancellationToken, Task<T>> action,
                                              CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        await _roundGate.WaitAsync(cancellationToken);
        try
        {
            return await action(cancellationToken);
        }
        finally
        {
            _roundGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<AccountingLotImportResult> ImportAsync(string? currency, IReadOnlyList<AccountingLotPoint> lots,
                                                             CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lots);
        if (!IsEnabled)
            throw new InvalidOperationException(
                "The financial book is off (Accounting:Profile=Financial and the books on): there are no lots to "
              + "replace");

        var code = currency is null ? Currency : AccountingFiat.NormalizeCurrency(currency);
        if (code != Currency)
            throw new ArgumentException($"The lots are in {code}, the financial book in {Currency}", nameof(currency));
        if (lots.Count == 0)
            throw new ArgumentException("No lots to import", nameof(lots));
        if (lots.Count > AccountingLotCsv.MaxLots)
            throw new ArgumentException($"At most {AccountingLotCsv.MaxLots} lots per import ({lots.Count} sent)",
                                        nameof(lots));

        for (var i = 0; i < lots.Count; i++)
        {
            if (lots[i] is null)
                throw new ArgumentException($"Lot {i + 1} is missing", nameof(lots));
            if (!AccountingLotCsv.TryValidate(lots[i], out var error))
                throw new ArgumentException($"Lot {i + 1}: {error}", nameof(lots));
        }

        var result = await RunExclusiveAsync(ct => ReplaceOpeningLotsAsync(lots, ct), cancellationToken);
        var projected = await ProjectAsync(cancellationToken);
        _logger.LogInformation(
            "Imported {Count} lots ({Msat} msat, {Cost} {Currency}) in place of the opening balances' lots; the "
          + "financial book was rebuilt ({Projected} entries)", result.Imported, result.ImportedMsat,
            AccountingFiat.Format(result.ImportedCost), Currency, projected);
        return result with { ProjectedEntries = projected };
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stopping.Dispose();
        Meter.Dispose();
    }

    /// <summary>Cancels the loop without waiting for it; prefer <see cref="DisposeAsync"/>.</summary>
    public void Dispose()
    {
        lock (_stateGate)
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

    #region Projection

    /// <summary>Projects up to <paramref name="maxPages"/> pages; the caller holds the round gate.</summary>
    private async Task<int> ProjectCoreAsync(int maxPages, CancellationToken cancellationToken)
    {
        var sink = Sink;
        var total = 0;
        var rollbacks = 0;
        FinancialProjectionRound? round = null;
        for (var page = 0; page < maxPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var scope = _scopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var books = unitOfWork.AccountingBooksDbRepository;
            using var held = await sink.EnterAsync(cancellationToken);

            var cursor = await books.GetCursorAsync(AccountingBook.Financial, cancellationToken);

            // Open entries above the cursor: the back-valuation (or a reclassification) asked for a replay from there
            long? rollbackTo = null;
            string? reason = null;
            IReadOnlyList<AccountingEntry> operational = [];
            if (await books.GetLastOpenEntrySeqAsync(AccountingBook.Financial, cancellationToken) > cursor)
            {
                rollbackTo = cursor + 1;
                reason = "entries after the cursor were valued or reclassified";
            }
            else
            {
                operational = await books.ListEntriesAsync(new AccountingEntryQuery(cursor, PageSize),
                                                           cancellationToken);
                if (operational.Count == 0)
                    break;

                // The round's state only when there is something to project (an idle round reads no lot)
                round ??= await FinancialProjectionRound.LoadAsync(unitOfWork, cursor, Method, Currency, sink,
                                                                   cancellationToken);
                if (round.RollbackTo is { } target)
                {
                    rollbackTo = target;
                    reason = round.RollbackReason;
                }
            }

            if (rollbackTo is { } from)
            {
                if (++rollbacks > MaxRollbacksPerCall)
                    throw new InvalidOperationException(
                        $"The financial book rolled back {MaxRollbacksPerCall} times in one round (last: {reason})");

                await books.RollbackOpenEntriesAsync(AccountingBook.Financial, from, cancellationToken);
                _rollbackCounter.Add(1);
                _logger.LogDebug("The financial book projects again from ledger sequence {From}: {Reason}", from,
                                 reason);
                round = null;
                page--;
                continue;
            }

            int written;
            try
            {
                written = await ProjectPageAsync(unitOfWork, round!, sink, operational, cancellationToken);
            }
            catch (RestartException restart)
            {
                // Nothing of the page is saved: the rollback forgets what the page staged and goes back to the fact
                if (++rollbacks > MaxRollbacksPerCall)
                    throw new InvalidOperationException(
                        $"The financial book rolled back {MaxRollbacksPerCall} times in one round");

                await books.RollbackOpenEntriesAsync(AccountingBook.Financial, restart.FromLedgerSeq,
                                                     cancellationToken);
                _rollbackCounter.Add(1);
                round = null;
                page--;
                continue;
            }
            catch (OperationFailedException)
            {
                // Logged at the entry: nothing of the page is saved, and the next round tries again
                return total;
            }

            total += written;
            if (operational.Count < PageSize)
                break;
        }

        return total;
    }

    private int PageSize => Math.Max(1, _options.SealBatchSize);

    /// <summary>One page: projects and saves it (entries, lots, reliefs, cursor), or throws before saving.</summary>
    private async Task<int> ProjectPageAsync(IUnitOfWork unitOfWork, FinancialProjectionRound round,
                                             IAccountingAdjustmentSink sink, IReadOnlyList<AccountingEntry> operational,
                                             CancellationToken cancellationToken)
    {
        var books = unitOfWork.AccountingBooksDbRepository;
        var first = operational[0].LedgerSeq - 1;
        var events = (await unitOfWork.AccountingEventDbRepository.ListAsync(
                          new AccountingEventQuery(first, operational.Count + 100), cancellationToken))
                    .Where(e => e.LedgerSeq is not null)
                    .GroupBy(e => e.LedgerSeq!.Value)
                    .ToDictionary(g => g.Key, g => g.First());
        var overrides = await unitOfWork.AccountingOverrideDbRepository.GetManyAsync(
                            operational.Select(e => e.EventKey).ToList(), cancellationToken);
        var engine = new ClassificationEngine(_chart,
                                              await unitOfWork.AccountingRuleDbRepository.ListAsync(
                                                  true, cancellationToken));
        var prices = await FinancialPriceWindow.LoadAsync(unitOfWork.AccountingPriceDbRepository, Currency,
                                                          _priceOptions.MaxAge,
                                                          operational.Select(e => e.OccurredAt).ToList(),
                                                          cancellationToken);
        var page = new PageState(unitOfWork, round, sink, engine, overrides, prices);

        var last = operational[0].LedgerSeq - 1;
        foreach (var entry in operational)
        {
            if (!events.TryGetValue(entry.LedgerSeq, out var accountingEvent))
            {
                Fail(entry, new InvalidOperationException($"The event of ledger sequence {entry.LedgerSeq} is missing"));
                throw new OperationFailedException();
            }

            try
            {
                await ProjectEntryAsync(page, entry, accountingEvent, cancellationToken);
            }
            catch (RestartException)
            {
                throw;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Fail(entry, e);
                throw new OperationFailedException();
            }

            last = entry.LedgerSeq;
        }

        await books.SetCursorAsync(AccountingBook.Financial, last, cancellationToken);
        await unitOfWork.SaveChangesAsync();
        ClearFailure();
        _projectedCounter.Add(operational.Count);
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Projected {Count} operational entries into the financial book up to ledger sequence "
                           + "{LedgerSeq}", operational.Count, last);
        return operational.Count;
    }

    private async Task ProjectEntryAsync(PageState page, AccountingEntry operational,
                                         AccountingEventModel accountingEvent, CancellationToken cancellationToken)
    {
        var round = page.Round;
        var books = page.UnitOfWork.AccountingBooksDbRepository;
        var locked = await page.Sink.GetLockingPeriodAsync(operational.OccurredAt, cancellationToken) is not null;
        var notes = new List<string?> { operational.Note };
        AccountingClassificationSource? classificationSource = AccountingClassificationSource.Default;
        long? ruleId = null;
        var extraFlags = AccountingEntryFlags.None;
        IReadOnlyList<AccountingPosting> lines;
        var cancelled = false;
        IReadOnlyList<AccountingPosting>? fixedPostings = null;
        var fixedFlags = AccountingEntryFlags.None;

        var reversedKey = operational.Kind == AccountingEventKind.Reversal
                       && accountingEvent.Details.TryGetValue(AccountingConfirmations.ReversesDetail, out var key)
                       && !string.IsNullOrEmpty(key)
                              ? key
                              : null;
        if (reversedKey is not null && !locked
                                    && await CancelledLinesAsync(page, reversedKey, cancellationToken) is { } original)
        {
            // The reversal of a fact cancelled in the open period: its exact negation, no lot
            fixedPostings = original.Postings.Select(Negate).ToList();
            fixedFlags = original.Flags & (AccountingEntryFlags.Unvalued | AccountingEntryFlags.PendingValuation);
            notes.Add($"cancels {reversedKey}");
            lines = [];
        }
        else if (reversedKey is not null
              && await ClosedFactLinesAsync(books, reversedKey, cancellationToken) is { } closedLines)
        {
            // The reversal of a closed period's fact: its lines negated at their values, in the open period
            lines = closedLines;
            extraFlags |= AccountingEntryFlags.Adjustment;
            notes.Add($"reverses {reversedKey} of a closed period");
        }
        else
        {
            page.Overrides.TryGetValue(operational.EventKey, out var accountingOverride);
            var classification = page.Engine.Classify(operational, accountingEvent, accountingOverride);
            lines = page.Engine.MapPostings(operational, accountingEvent, classification);
            classificationSource = classification.Source;
            ruleId = classification.RuleId;
            if (classification.IsUnclassified)
                extraFlags |= AccountingEntryFlags.Unclassified;

            if (round.CancelledFacts.TryGetValue(operational.EventKey, out var reversalKey) && !locked)
            {
                cancelled = true;
                notes.Add(FinancialProjectionRound.CancelledNotePrefix + reversalKey);
            }
        }

        if (operational.EventKey == AccountingEventKeys.Cutover() && round.HasImportedLots)
        {
            var (importLines, pending) = await ImportedBasisLinesAsync(page, cancellationToken);
            fixedPostings = importLines;
            fixedFlags = pending ? AccountingEntryFlags.GainPending : AccountingEntryFlags.None;
            notes.Add("the imported lots' basis (D-A9)");
        }

        // Plan the entry (or take the fixed lines)
        FinancialEntryPlan plan;
        if (fixedPostings is not null)
        {
            plan = new FinancialEntryPlan(fixedPostings, fixedFlags, [], null);
        }
        else
        {
            var price = lines.Count == 0 || lines.All(l => l.FiatAmount is not null)
                            ? null
                            : await UsablePriceAsync(page.Prices, operational.OccurredAt, locked, cancellationToken);
            var at = locked ? _timeProvider.GetUtcNow() : operational.OccurredAt;
            plan = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(lines, price, at)
            {
                IsOpeningBalance = operational.Kind == AccountingEventKind.OpeningBalance,
                OpeningLotsImported = round.HasImportedLots,
                Cancelled = cancelled
            }, round.Pool, _chart);
        }

        if (plan.ShortfallMsat > 0)
        {
            _shortfallCounter.Add(plan.ShortfallMsat);
            _logger.LogWarning("Financial book: {EventKey} disposes of {Shortfall} msat no lot covers; its cost is "
                             + "taken as its proceeds", operational.EventKey, plan.ShortfallMsat);
        }

        notes.Add(plan.Note);
        var note = JoinNotes(notes);
        var flags = plan.Flags | extraFlags;
        if (cancelled)
            round.CancelledLines[operational.EventKey] = plan.Postings;

        if (locked)
        {
            // A late fact of a closed period: an adjustment of the open period (A3-T5), never replayed
            if (plan.Postings.Count == 0)
                return;

            var staged = await page.Sink.StageAdjustmentAsync(page.UnitOfWork, new AccountingAdjustment(
                                                                  AccountingAdjustmentReason.LateFact,
                                                                  operational.LedgerSeq, operational.EventKey,
                                                                  operational.Kind, operational.OccurredAt,
                                                                  plan.Postings)
            {
                ChannelId = operational.ChannelId,
                PaymentHash = operational.PaymentHash,
                Note = note,
                Flags = flags & ~AccountingEntryFlags.PendingValuation,
                Classification = classificationSource,
                RuleId = ruleId
            }, cancellationToken);
            if (staged is not null)
                await ApplyLotsAsync(page, plan, staged.LedgerSeq, staged.Adjustment, staged.OccurredAt,
                                     cancellationToken);
            return;
        }

        var financial = new AccountingEntry(operational.LedgerSeq, operational.EventKey, operational.Kind,
                                            operational.OccurredAt, operational.ChannelId, operational.PaymentHash,
                                            plan.Postings, note)
        {
            Book = AccountingBook.Financial,
            Flags = flags,
            Classification = classificationSource,
            RuleId = ruleId
        };
        await books.AddEntryAsync(financial, cancellationToken);
        if (operational.Kind == AccountingEventKind.OpeningBalance)
            page.StagedOpenings.Add(financial);
        await ApplyLotsAsync(page, plan, operational.LedgerSeq, 0, operational.OccurredAt, cancellationToken);
    }

    /// <summary>Stages the plan's reliefs and lot and keeps the pool in step.</summary>
    private static async Task ApplyLotsAsync(PageState page, FinancialEntryPlan plan, long ledgerSeq, int adjustment,
                                             DateTimeOffset at, CancellationToken cancellationToken)
    {
        var lots = page.UnitOfWork.AccountingLotDbRepository;
        foreach (var take in plan.Reliefs)
        {
            if (take.LotId <= 0)
                continue;

            var relieved = page.Round.Pool.Relieve(take.LotId, take.Msat);
            await lots.UpdateLotAsync(relieved, cancellationToken);
            lots.AddRelief(new AccountingLotRelief(0, take.LotId, ledgerSeq, adjustment, at, take.Msat, take.Cost,
                                                   take.Proceeds, null));
        }

        if (plan.NewLot is not { } spec)
            return;

        var lot = new AccountingLot(0, at, spec.Origin, ledgerSeq, adjustment, null, null, spec.Msat, spec.Msat,
                                    spec.Cost, spec.Currency, spec.PriceId, spec.BasisEstimated, null);
        var id = await lots.AddLotAsync(lot, cancellationToken);
        page.Round.Pool.Add(lot with { Id = id });
    }

    /// <summary>
    /// The price of an entry (D-A11): the nearest stored one at or before its time within the maximum age; with a
    /// fetching source, one older than the entry's own hour only once the hour is
    /// <see cref="PriceValuationService.RecentHourWait"/> old (the back-valuation's rule), or at once for a late fact.
    /// </summary>
    private async Task<AccountingPrice?> UsablePriceAsync(FinancialPriceWindow prices, DateTimeOffset at,
                                                          bool anyUsable, CancellationToken cancellationToken)
    {
        var price = await prices.FindAsync(at, cancellationToken);
        if (price is null || anyUsable || !_canFetch)
            return price;

        var hour = AccountingValuation.HourStart(at);
        return price.Time >= hour || _timeProvider.GetUtcNow() >= hour + PriceValuationService.RecentHourWait
                   ? price
                   : null;
    }

    /// <summary>
    /// The financial entry of a fact cancelled in the open period (projected in this round, or saved with the cancelled
    /// note), or null when the fact is not cancelled. A fact the book projected in the open period without being
    /// cancelled restarts the page from it (its reversal came after the round looked).
    /// </summary>
    private static async Task<AccountingEntry?> CancelledLinesAsync(PageState page, string reversedKey,
                                                                    CancellationToken cancellationToken)
    {
        if (page.Round.CancelledLines.TryGetValue(reversedKey, out var lines))
            return new AccountingEntry(0, reversedKey, AccountingEventKind.Reversal, default, null, null, lines)
            {
                Flags = lines.Any(l => l.AmountMsat != 0 && l.FiatAmount is null)
                            ? AccountingEntryFlags.Unvalued | AccountingEntryFlags.PendingValuation
                            : AccountingEntryFlags.None
            };

        var saved = await page.UnitOfWork.AccountingBooksDbRepository.GetEntriesByKeyAsync(
                        AccountingBook.Financial, reversedKey, cancellationToken);
        var projected = saved.FirstOrDefault(e => e.Adjustment == 0);
        if (projected is null || projected.ClosedPeriodId is not null)
            return null;

        if (projected.Note?.Contains(FinancialProjectionRound.CancelledNotePrefix, StringComparison.Ordinal) == true)
            return projected;

        if (await page.Sink.GetLockingPeriodAsync(projected.OccurredAt, cancellationToken) is not null)
            return null;

        throw new RestartException(projected.LedgerSeq);
    }

    /// <summary>The negated lines (at their values) of a closed period's fact, or null when the fact is not in a
    /// closed period of the book.</summary>
    private static async Task<IReadOnlyList<AccountingPosting>?> ClosedFactLinesAsync(
        IAccountingBooksDbRepository books, string reversedKey, CancellationToken cancellationToken)
    {
        var entries = await books.GetEntriesByKeyAsync(AccountingBook.Financial, reversedKey, cancellationToken);
        if (entries.Count == 0 || entries.Any(e => e.Adjustment == 0 && e.ClosedPeriodId is null))
            return null;

        // Every entry of the fact (a late fact's adjustment, a reclassification) summed per account, msat lines only
        var sums = entries.SelectMany(e => e.Postings)
                          .GroupBy(p => (p.Account, p.AccountName))
                          .Select(g => (g.Key.Account, g.Key.AccountName, Msat: g.Sum(p => p.AmountMsat),
                                        Valued: g.All(p => p.FiatAmount is not null),
                                        Fiat: g.Sum(p => p.FiatAmount ?? 0m),
                                        Currency: g.Select(p => p.FiatCurrency).FirstOrDefault(c => c is not null)))
                          .Where(s => s.Msat != 0)
                          .ToList();
        var valued = sums.All(s => s.Valued && s.Currency is not null);
        return sums.Select(s => new AccountingPosting(s.Account, -s.Msat)
        {
            AccountName = s.AccountName,
            FiatAmount = valued ? -s.Fiat : null,
            FiatCurrency = valued ? s.Currency : null
        }).ToList();
    }

    /// <summary>
    /// The cutover marker's lines after a lot import: <c>assets:cost-basis</c> against the opening balances by the
    /// imported cost less the opening balances' market value; pending when an opening balance or a lot is unvalued.
    /// </summary>
    private async Task<(IReadOnlyList<AccountingPosting> Lines, bool Pending)> ImportedBasisLinesAsync(
        PageState page, CancellationToken cancellationToken)
    {
        var books = page.UnitOfWork.AccountingBooksDbRepository;
        var openings = new List<AccountingEntry>(page.StagedOpenings);
        var after = 0L;
        while (true)
        {
            var saved = await books.ListEntriesAsync(new AccountingEntryQuery(after, 500,
                                                                              Kinds: [AccountingEventKind.OpeningBalance])
            {
                Book = AccountingBook.Financial
            }, cancellationToken);
            openings.AddRange(saved.Where(e => e.Adjustment == 0));
            if (saved.Count < 500)
                break;

            after = saved[^1].LedgerSeq;
        }

        var assetLines = openings.SelectMany(e => e.Postings).Where(p => FinancialLotRules.IsAsset(p.Account)
                                                                      && p.AmountMsat != 0).ToList();
        var lots = page.Round.ImportedLots;
        if (assetLines.Any(p => p.FiatAmount is null || p.FiatCurrency != Currency)
         || lots.Any(l => l.FiatCost is null || l.FiatCurrency != Currency))
            return ([], true);

        var difference = lots.Sum(l => l.FiatCost!.Value) - assetLines.Sum(p => p.FiatAmount!.Value);
        if (difference == 0m)
            return ([], false);

        return (
        [
            new AccountingPosting(AccountRole.Opening, 0)
            {
                AccountName = _chart[FinancialAccount.CostBasis], FiatAmount = difference, FiatCurrency = Currency
            },
            new AccountingPosting(AccountRole.Opening, 0)
            {
                AccountName = _chart[FinancialAccount.Opening], FiatAmount = -difference, FiatCurrency = Currency
            }
        ], false);
    }

    private static AccountingPosting Negate(AccountingPosting posting) =>
        posting with { AmountMsat = checked(-posting.AmountMsat), FiatAmount = -posting.FiatAmount };

    private static string? JoinNotes(IEnumerable<string?> notes)
    {
        var text = string.Join("; ", notes.Where(n => !string.IsNullOrEmpty(n)));
        if (text.Length == 0)
            return null;

        return text.Length > AccountingSchemaLimits.NoteMaxLength ? text[..AccountingSchemaLimits.NoteMaxLength] : text;
    }

    private void Fail(AccountingEntry entry, Exception failure)
    {
        _failureCounter.Add(1, new KeyValuePair<string, object?>("kind", entry.Kind.ToString()));
        _projectionError = $"{entry.EventKey} (ledger sequence {entry.LedgerSeq}): {failure.Message}";
        if (_reportedFailureKey != entry.EventKey)
        {
            _reportedFailureKey = entry.EventKey;
            _logger.LogCritical(failure,
                                "The financial book cannot project {EventKey} ({Kind}, ledger sequence {LedgerSeq}): it "
                              + "stops there and retries every round; it never skips an entry", entry.EventKey,
                                entry.Kind, entry.LedgerSeq);
        }
    }

    private void ClearFailure()
    {
        if (_reportedFailureKey is not null)
            _logger.LogInformation("The financial book got past {EventKey}", _reportedFailureKey);

        _reportedFailureKey = null;
        _projectionError = null;
    }

    private async Task RunAsync()
    {
        var token = _stopping.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _roundGate.WaitAsync(token);
                try
                {
                    await ProjectCoreAsync(MaxBatchesPerRound, token);
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
            catch (OperationFailedException)
            {
                // Logged at the entry; the next round tries again
            }
            catch (Exception e)
            {
                _logger.LogError(e, "The financial book's round failed");
            }

            try
            {
                await Task.Delay(Interval, _timeProvider, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    #endregion

    #region Lot import

    private async Task<AccountingLotImportResult> ReplaceOpeningLotsAsync(IReadOnlyList<AccountingLotPoint> lots,
                                                                         CancellationToken cancellationToken)
    {
        using var held = await Sink.EnterAsync(cancellationToken);
        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        if (await unitOfWork.AccountingPeriodDbRepository.GetLastClosedAsync(cancellationToken) is { } closed)
            throw new InvalidOperationException(
                $"The period {closed.PeriodId} is closed: after the first close the opening lots change only through "
              + "an adjustment (D-A9)");

        var openingMsat = await SumOpeningBalancesAsync(unitOfWork.AccountingBooksDbRepository, cancellationToken);
        if (openingMsat <= 0)
            throw new InvalidOperationException(
                "The books hold no opening balance (the cutover found nothing): there are no opening lots to replace");

        var importedMsat = lots.Aggregate(0L, (sum, lot) => checked(sum + lot.Msat));
        var difference = openingMsat - importedMsat;
        if (Math.Abs(difference) >= 1_000)
            throw new ArgumentException(
                $"The lots hold {importedMsat} msat, the opening balances {openingMsat} msat: they must agree to the "
              + "sat (the last lot takes the msat difference)", nameof(lots));

        var adjusted = lots.ToList();
        if (difference != 0)
        {
            var lastLot = adjusted[^1];
            if (lastLot.Msat + difference <= 0)
                throw new ArgumentException("The last lot is too small to take the msat difference", nameof(lots));
            adjusted[^1] = lastLot with { Msat = lastLot.Msat + difference };
        }

        await unitOfWork.AccountingBooksDbRepository.ResetToCloseAsync(AccountingBook.Financial,
                                                                       new AccountingBookReset(0, []),
                                                                       cancellationToken);
        var lotRepository = unitOfWork.AccountingLotDbRepository;
        var replaced = await lotRepository.DeleteLotsByOriginAsync(AccountingLotOrigin.Import, cancellationToken);
        foreach (var lot in adjusted)
            await lotRepository.AddLotAsync(new AccountingLot(0, lot.Time, AccountingLotOrigin.Import, null, 0, null,
                                                              null, lot.Msat, lot.Msat, lot.Cost, Currency, null,
                                                              false, null), cancellationToken);
        await unitOfWork.SaveChangesAsync();

        return new AccountingLotImportResult(Currency, adjusted.Count, openingMsat, adjusted.Sum(l => l.Cost),
                                             openingMsat, replaced, 0)
        {
            AdjustedMsat = difference
        };
    }

    /// <summary>The msat of every opening balance of the operational book (the cutover's buckets).</summary>
    private static async Task<long> SumOpeningBalancesAsync(IAccountingBooksDbRepository books,
                                                            CancellationToken cancellationToken)
    {
        var total = 0L;
        var after = 0L;
        while (true)
        {
            var page = await books.ListEntriesAsync(new AccountingEntryQuery(after, 500,
                                                                             Kinds: [AccountingEventKind.OpeningBalance]),
                                                    cancellationToken);
            foreach (var entry in page)
                total = checked(total + entry.Postings.Where(p => FinancialLotRules.IsAsset(p.Account))
                                                      .Sum(p => p.AmountMsat));
            if (page.Count < 500)
                return total;

            after = page[^1].LedgerSeq;
        }
    }

    #endregion

    /// <summary>What one page shares between its entries.</summary>
    private sealed class PageState(
        IUnitOfWork unitOfWork,
        FinancialProjectionRound round,
        IAccountingAdjustmentSink sink,
        ClassificationEngine engine,
        IReadOnlyDictionary<string, AccountingOverride> overrides,
        FinancialPriceWindow prices)
    {
        public IUnitOfWork UnitOfWork { get; } = unitOfWork;
        public FinancialProjectionRound Round { get; } = round;
        public IAccountingAdjustmentSink Sink { get; } = sink;
        public ClassificationEngine Engine { get; } = engine;
        public IReadOnlyDictionary<string, AccountingOverride> Overrides { get; } = overrides;
        public FinancialPriceWindow Prices { get; } = prices;
        public List<AccountingEntry> StagedOpenings { get; } = [];
    }

    /// <summary>A page found a fact projected before its reversal: roll back to it and start again.</summary>
    private sealed class RestartException(long fromLedgerSeq) : Exception
    {
        public long FromLedgerSeq { get; } = fromLedgerSeq;
    }

    /// <summary>A page stopped at an entry it cannot project (already logged).</summary>
    private sealed class OperationFailedException : Exception;
}