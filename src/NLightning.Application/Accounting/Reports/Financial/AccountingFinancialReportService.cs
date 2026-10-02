using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Accounting.Reports.Financial;

using Domain.Accounting.Books;
using Domain.Accounting.Books.Reports;
using Domain.Accounting.Financial;
using Domain.Accounting.Financial.Classification;
using Domain.Accounting.Financial.Reports;
using Domain.Accounting.Interfaces;
using Domain.Persistence.Interfaces;

/// <summary>
/// The reports of the financial book (NL-602 A3-T6, plan §6.2; IPC 43 with <c>--book financial</c>): balance sheet and
/// income statement from the book's postings in msat and fiat, realized gains from the lot reliefs, the open lots and
/// their unrealized gains at a price, the entries and postings waiting for review, and the risk-weighted capital of a
/// live snapshot.
/// </summary>
/// <remarks>
/// <para>Every book report refuses when the books or the financial book are off, then seals, projects both books and
/// reads through a scope of its own (<see cref="AccountingFinancialAccess"/>). Amounts are exact: the fiat sums are the
/// sums of the stored 8-place values (D-A11), rounded to the currency's minor unit only by the printer.</para>
/// <para>A financial line's account is its <see cref="AccountingPosting.AccountName"/>; lines of several roles with the
/// same name are one account in the statements. The category is the root of the name
/// (<see cref="AccountingFiat.CategoryOf"/>).</para>
/// </remarks>
public sealed class AccountingFinancialReportService : IAccountingFinancialReports
{
    /// <summary>The page size of the relief reads.</summary>
    internal const int PageSize = 1_000;

    /// <summary>The largest page of the lots, register and unvalued lists.</summary>
    public const int MaxTake = 1_000;

    private static readonly TimeSpan s_longTerm = TimeSpan.FromDays(365);

    private readonly AccountingFinancialAccess _access;
    private readonly FinancialChart _chart;
    private readonly AccountNames _names;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly INodeSnapshotSource? _snapshotSource;
    private readonly TimeProvider _timeProvider;
    private readonly AccountingRiskWeights _weights;

    public AccountingFinancialReportService(IServiceScopeFactory scopeFactory, IAccountingBooks? books,
                                            ILogger<AccountingFinancialReportService> logger,
                                            IOptions<AccountingOptions>? options = null,
                                            IAccountingEventSealer? sealer = null,
                                            IFinancialBooksProjector? projection = null,
                                            INodeSnapshotSource? snapshotSource = null,
                                            TimeProvider? timeProvider = null,
                                            AccountingRiskWeights? weights = null)
    {
        _scopeFactory = scopeFactory;
        _access = new AccountingFinancialAccess(books, sealer, projection, logger);
        _names = (options?.Value ?? new AccountingOptions()).GetAccountNames();
        _chart = (options?.Value ?? new AccountingOptions()).GetFinancialChart();
        _snapshotSource = snapshotSource;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _weights = weights ?? AccountingRiskWeights.Default;
    }

    /// <inheritdoc/>
    public async Task<AccountingFinancialBalanceSheet> GetBalanceSheetAsync(DateTimeOffset? at, string? currency,
                                                                            decimal? price,
                                                                            bool withMarketValue = false,
                                                                            CancellationToken cancellationToken = default)
    {
        var code = AccountingFiat.NormalizeCurrency(currency);
        ThrowIfBadPrice(price);
        await _access.PrepareAsync(cancellationToken);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var books = unitOfWork.AccountingBooksDbRepository;
        var cursor = await books.GetCursorAsync(AccountingBook.Financial, cancellationToken);
        var sums = Merge(await books.SumAccountPostingsAsync(AccountingBook.Financial, null, at, code,
                                                             cancellationToken));
        var reportPrice = price is { } given
                              ? new AccountingReportPrice(given, code, null, null)
                              : withMarketValue
                                  ? await LatestPriceAsync(unitOfWork, code, at, cancellationToken)
                                  : null;

        var assets = Lines(sums, AccountingAccountCategory.Assets, 1)
                    .Select(l => reportPrice is null
                                     ? l
                                     : l with
                                     {
                                         MarketValue = AccountingFiat.Value(l.AmountMsat,
                                                                                   reportPrice.PricePerBitcoin)
                                     })
                    .ToList();
        var earnings = sums.Where(s => s.Category is AccountingAccountCategory.Income
                                                    or AccountingAccountCategory.Expenses)
                           .ToList();
        return new AccountingFinancialBalanceSheet(at, cursor, code, assets,
                                                   Lines(sums, AccountingAccountCategory.Liabilities, -1),
                                                   Lines(sums, AccountingAccountCategory.Equity, -1),
                                                   -earnings.Sum(s => s.AmountMsat), -earnings.Sum(s => s.FiatAmount))
        {
            Price = reportPrice,
            UnvaluedPostings = sums.Sum(s => s.UnvaluedPostings)
        };
    }

    /// <inheritdoc/>
    public async Task<AccountingFinancialIncomeStatement> GetIncomeStatementAsync(
        DateTimeOffset? since, DateTimeOffset? until, string? currency, CancellationToken cancellationToken = default)
    {
        ThrowIfEmptyWindow(since, until);
        var code = AccountingFiat.NormalizeCurrency(currency);
        await _access.PrepareAsync(cancellationToken);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var books = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingBooksDbRepository;
        var cursor = await books.GetCursorAsync(AccountingBook.Financial, cancellationToken);
        var sums = Merge(await books.SumAccountPostingsAsync(AccountingBook.Financial, since, until, code,
                                                             cancellationToken));

        return new AccountingFinancialIncomeStatement(since, until, cursor, code,
                                                      Lines(sums, AccountingAccountCategory.Income, -1),
                                                      Lines(sums, AccountingAccountCategory.Expenses, 1));
    }

    /// <inheritdoc/>
    public async Task<AccountingRealizedGainsReport> GetRealizedGainsAsync(
        DateTimeOffset? since, DateTimeOffset? until, AccountingGainsGrouping grouping, string? currency,
        CancellationToken cancellationToken = default)
    {
        ThrowIfEmptyWindow(since, until);
        if (!Enum.IsDefined(grouping))
            throw new ArgumentOutOfRangeException(nameof(grouping), grouping, "Unknown grouping.");

        var code = AccountingFiat.NormalizeCurrency(currency);
        await _access.PrepareAsync(cancellationToken);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var lotsRepository = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingLotDbRepository;

        var lots = new Dictionary<long, AccountingLot?>();
        var periods = new SortedDictionary<DateTimeOffset, GainsAccumulator>();
        var total = new GainsAccumulator("total", since, until);
        var after = 0L;
        while (true)
        {
            var page = await lotsRepository.ListReliefsAsync(since, until, after, PageSize, cancellationToken);
            foreach (var relief in page)
            {
                // A move between our buckets or a debt's settlement realizes nothing (NL-657)
                if (!relief.IsDisposal)
                    continue;

                if (!lots.TryGetValue(relief.LotId, out var lot))
                    lots[relief.LotId] = lot = await lotsRepository.GetLotAsync(relief.LotId, cancellationToken);

                var (name, start, end) = PeriodOf(relief.RelievedAt, grouping, since, until);
                if (!periods.TryGetValue(start ?? DateTimeOffset.MinValue, out var period))
                    periods[start ?? DateTimeOffset.MinValue] = period = new GainsAccumulator(name, start, end);

                period.Add(relief, lot, code);
                total.Add(relief, lot, code);
            }

            if (page.Count < PageSize)
                break;

            after = page[^1].Id;
        }

        return new AccountingRealizedGainsReport(since, until, code, grouping,
                                                 periods.Values.Select(p => p.ToLine()).ToList(), total.ToLine());
    }

    /// <inheritdoc/>
    public async Task<AccountingLotsReport> GetLotsAsync(long afterLotId, int take, string? currency, decimal? price,
                                                         bool requirePrice,
                                                         CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterLotId);
        if (take is <= 0 or > MaxTake)
            throw new ArgumentOutOfRangeException(nameof(take), take, $"The page must hold 1 to {MaxTake} lots.");
        ThrowIfBadPrice(price);

        var code = AccountingFiat.NormalizeCurrency(currency);
        await _access.PrepareAsync(cancellationToken);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var reportPrice = price is { } given
                              ? new AccountingReportPrice(given, code, null, null)
                              : requirePrice
                                  ? await LatestPriceAsync(unitOfWork, code, null, cancellationToken)
                                 ?? throw new InvalidOperationException(
                                        $"No {code} price is stored yet: give one with --price.")
                                  : null;

        var open = (await unitOfWork.AccountingLotDbRepository.ListOpenLotsAsync(null, cancellationToken))
                  .Where(l => !l.IsDebt)
                  .OrderBy(l => l.Id)
                  .Select(l => Line(l, code, reportPrice))
                  .ToList();
        var page = open.Where(l => l.Lot.Id > afterLotId).Take(take).ToList();
        var valued = open.Where(l => l.RemainingCostBasis is not null).ToList();
        var totals = new AccountingLotTotals
        {
            OpenLots = open.Count,
            RemainingMsat = open.Sum(l => l.Lot.RemainingMsat),
            CostBasis = valued.Sum(l => l.RemainingCostBasis!.Value),
            UnvaluedMsat = open.Where(l => l.RemainingCostBasis is null).Sum(l => l.Lot.RemainingMsat),
            UnvaluedLots = open.Count - valued.Count,
            BasisEstimatedMsat = open.Where(l => l.Lot.BasisEstimated).Sum(l => l.Lot.RemainingMsat),
            MarketValue = reportPrice is null ? null : open.Sum(l => l.MarketValue ?? 0m),
            UnrealizedGain = reportPrice is null ? null : valued.Sum(l => l.UnrealizedGain ?? 0m)
        };

        var hasMore = page.Count == take && open.Any(l => l.Lot.Id > page[^1].Lot.Id);
        return new AccountingLotsReport(code, reportPrice, page, page.Count > 0 ? page[^1].Lot.Id : afterLotId, hasMore,
                                        totals);
    }

    /// <inheritdoc/>
    public async Task<AccountingFinancialRegister> GetRegisterAsync(AccountingEntryQuery query,
                                                                    CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegative(query.AfterLedgerSeq);
        if (query.Take is <= 0 or > MaxTake)
            throw new ArgumentOutOfRangeException(nameof(query), query.Take,
                                                  $"The page must hold 1 to {MaxTake} entries.");
        ThrowIfEmptyWindow(query.Since, query.Until);
        await _access.PrepareAsync(cancellationToken);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var books = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingBooksDbRepository;
        var cursor = await books.GetCursorAsync(AccountingBook.Financial, cancellationToken);
        var entries = await books.ListEntriesAsync(query with { Book = AccountingBook.Financial }, cancellationToken);
        if (entries.Count == 0)
            return new AccountingFinancialRegister(entries, query.AfterLedgerSeq, query.AfterAdjustment, false, cursor);

        // The page's position is the read's, even when the review listing leaves entries out
        var listed = query.WithFlags.HasFlag(AccountingEntryFlags.Unclassified)
                         ? await StillUnclassifiedAsync(books, entries, cancellationToken)
                         : entries;
        return new AccountingFinancialRegister(listed, entries[^1].LedgerSeq, entries[^1].Adjustment,
                                               entries.Count == query.Take, cursor);
    }

    /// <summary>
    /// The unclassified entries still waiting for review (NL-667): a closed entry reclassified later by an adjustment
    /// in the open period (NL-660) keeps its flag, so it is listed only while a classifiable line of it is left in an
    /// unclassified account (<see cref="AccountingReclassification.IsStillUnclassified"/>); open entries are projected
    /// again when reclassified, so their flag is current.
    /// </summary>
    private async Task<IReadOnlyList<AccountingEntry>> StillUnclassifiedAsync(IAccountingBooksDbRepository books,
                                                                             IReadOnlyList<AccountingEntry> entries,
                                                                             CancellationToken cancellationToken)
    {
        var listed = new List<AccountingEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.ClosedPeriodId is not null
             && !AccountingReclassification.IsStillUnclassified(
                    await books.GetEntriesByKeyAsync(AccountingBook.Financial, entry.EventKey, cancellationToken),
                    _chart))
                continue;

            listed.Add(entry);
        }

        return listed;
    }

    /// <inheritdoc/>
    public async Task<AccountingUnvaluedReport> GetUnvaluedAsync(int take, CancellationToken cancellationToken = default)
    {
        if (take is <= 0 or > MaxTake)
            throw new ArgumentOutOfRangeException(nameof(take), take, $"The page must hold 1 to {MaxTake} postings.");

        await _access.PrepareAsync(cancellationToken);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var books = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingBooksDbRepository;
        var cursor = await books.GetCursorAsync(AccountingBook.Financial, cancellationToken);
        var postings = await books.ListUnvaluedPostingsAsync(AccountingBook.Financial, take + 1, cancellationToken);

        return new AccountingUnvaluedReport(postings.Take(take).ToList(), postings.Count > take, cursor);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">No snapshot source is registered (not a node).</exception>
    public async Task<AccountingRiskCapitalReport> GetRiskCapitalAsync(string? currency, decimal? price,
                                                                       CancellationToken cancellationToken = default)
    {
        var code = AccountingFiat.NormalizeCurrency(currency);
        ThrowIfBadPrice(price);
        var source = _snapshotSource
                  ?? throw new InvalidOperationException("The node's balances are not available here.");
        var snapshot = await source.TakeSnapshotAsync(cancellationToken);

        AccountingReportPrice? reportPrice;
        if (price is { } given)
        {
            reportPrice = new AccountingReportPrice(given, code, null, null);
        }
        else
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            reportPrice = await LatestPriceAsync(scope.ServiceProvider.GetRequiredService<IUnitOfWork>(), code, null,
                                                 cancellationToken);
        }

        return AccountingRiskCapital.Build(snapshot, _weights) with { Price = reportPrice };
    }

    // The latest stored price at or before the time (now when null), however old (the report shows its time)
    private async Task<AccountingReportPrice?> LatestPriceAsync(IUnitOfWork unitOfWork, string currency,
                                                                DateTimeOffset? at,
                                                                CancellationToken cancellationToken)
    {
        var time = at ?? _timeProvider.GetUtcNow();
        var stored = await unitOfWork.AccountingPriceDbRepository.GetAtOrBeforeAsync(
                         currency, time, AccountingFiat.StoredPriceLookback, cancellationToken);
        return stored is null ? null : new AccountingReportPrice(stored.Price, stored.Currency, stored.Time, stored.Id);
    }

    private static AccountingLotLine Line(AccountingLot lot, string currency, AccountingReportPrice? price)
    {
        decimal? costBasis = null;
        if (lot.FiatCost is { } cost && lot.OriginalMsat > 0
                                     && string.Equals(lot.FiatCurrency, currency, StringComparison.Ordinal))
            costBasis = lot.RemainingMsat == lot.OriginalMsat
                            ? cost
                            : AccountingFiat.RoundStored(cost * lot.RemainingMsat / lot.OriginalMsat);

        decimal? market = price is null ? null : AccountingFiat.Value(lot.RemainingMsat, price.PricePerBitcoin);
        return new AccountingLotLine(lot, costBasis, market);
    }

    private sealed record MergedSum(
        AccountRole Account,
        string Name,
        AccountingAccountCategory Category,
        long AmountMsat,
        decimal FiatAmount,
        int UnvaluedPostings);

    // One line per financial account name (several roles may post to one), with its category
    private List<MergedSum> Merge(IReadOnlyList<AccountingAccountSum> sums) =>
        sums.GroupBy(s => string.IsNullOrWhiteSpace(s.AccountName) ? _names[s.Account] : s.AccountName,
                     StringComparer.Ordinal)
            .Select(g =>
             {
                 var role = g.Min(s => s.Account);
                 return new MergedSum(role, g.Key, AccountingFiat.CategoryOf(g.Key, role), g.Sum(s => s.AmountMsat),
                                      g.Sum(s => s.FiatAmount), g.Sum(s => s.UnvaluedPostings));
             })
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

    private static List<AccountingFinancialAccountLine> Lines(IEnumerable<MergedSum> sums,
                                                              AccountingAccountCategory category, int sign) =>
        sums.Where(s => s.Category == category)
            .Select(s => new AccountingFinancialAccountLine(s.Account, s.Name, s.Category, sign * s.AmountMsat,
                                                            sign * s.FiatAmount, s.UnvaluedPostings))
            .ToList();

    /// <summary>The calendar period of a time (UTC), clipped to the report's window for the total.</summary>
    internal static (string Name, DateTimeOffset? Start, DateTimeOffset? End) PeriodOf(
        DateTimeOffset time, AccountingGainsGrouping grouping, DateTimeOffset? since, DateTimeOffset? until)
    {
        var utc = time.UtcDateTime;
        switch (grouping)
        {
            case AccountingGainsGrouping.Month:
                var month = new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
                return (month.ToString("yyyy-MM", CultureInfo.InvariantCulture), month, month.AddMonths(1));
            case AccountingGainsGrouping.Quarter:
                var quarter = (utc.Month - 1) / 3;
                var quarterStart = new DateTimeOffset(utc.Year, quarter * 3 + 1, 1, 0, 0, 0, TimeSpan.Zero);
                return (string.Create(CultureInfo.InvariantCulture, $"{utc.Year:D4}-Q{quarter + 1}"), quarterStart,
                        quarterStart.AddMonths(3));
            case AccountingGainsGrouping.Year:
                var year = new DateTimeOffset(utc.Year, 1, 1, 0, 0, 0, TimeSpan.Zero);
                return (year.ToString("yyyy", CultureInfo.InvariantCulture), year, year.AddYears(1));
            default:
                return ("total", since, until);
        }
    }

    private static void ThrowIfEmptyWindow(DateTimeOffset? since, DateTimeOffset? until)
    {
        if (since is { } start && until is { } end && end <= start)
            throw new ArgumentException("until must be after since.", nameof(until));
    }

    private static void ThrowIfBadPrice(decimal? price)
    {
        if (price is <= 0m)
            throw new ArgumentOutOfRangeException(nameof(price), price, "A price must be above zero.");
    }

    private sealed class GainsAccumulator(string name, DateTimeOffset? start, DateTimeOffset? end)
    {
        private long _basisEstimatedMsat;
        private decimal _cost;
        private long _disposed;
        private decimal _longTerm;
        private int _pending;
        private long _pendingMsat;
        private decimal _proceeds;
        private int _reliefs;
        private decimal _shortTerm;

        public void Add(AccountingLotRelief relief, AccountingLot? lot, string currency)
        {
            _reliefs++;
            _disposed += relief.Msat;
            if (lot?.BasisEstimated == true)
                _basisEstimatedMsat += relief.Msat;

            var sameCurrency = lot?.FiatCurrency is null
                            || string.Equals(lot.FiatCurrency, currency, StringComparison.Ordinal);
            if (relief.FiatCostRelieved is not { } cost || relief.Proceeds is not { } proceeds || !sameCurrency)
            {
                _pending++;
                _pendingMsat += relief.Msat;
                return;
            }

            _cost += cost;
            _proceeds += proceeds;
            var gain = proceeds - cost;
            if (lot is not null && relief.RelievedAt - lot.HeldSinceOrAcquired > s_longTerm)
                _longTerm += gain;
            else
                _shortTerm += gain;
        }

        public AccountingRealizedGainsLine ToLine() => new(name, start, end)
        {
            Reliefs = _reliefs,
            DisposedMsat = _disposed,
            CostBasis = _cost,
            Proceeds = _proceeds,
            ShortTermGain = _shortTerm,
            LongTermGain = _longTerm,
            PendingValuation = _pending,
            PendingValuationMsat = _pendingMsat,
            BasisEstimatedMsat = _basisEstimatedMsat
        };
    }
}