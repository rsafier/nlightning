namespace NLightning.Application.Accounting.Financial;

using Domain.Accounting.Books;
using Domain.Accounting.Enums;
using Domain.Accounting.Financial;
using Domain.Accounting.Financial.Lots;
using Domain.Accounting.Models;
using Domain.Accounting.Prices;
using Domain.Accounting.Services;
using Domain.Persistence.Interfaces;

/// <summary>
/// What one projection round of the financial book keeps in memory (NL-602 A3-T4): the open lots, the imported lots and
/// the reversals of the facts not projected yet. Loaded at the start of a round (and again after a rollback); the
/// projector keeps the pool for the next round only while the saved lots still have its fingerprint, and never across a
/// rollback or an exclusive action (NL-658).
/// </summary>
internal sealed class FinancialProjectionRound
{
    /// <summary>The note a fact projected as cancelled by its reversal carries (both in the open period).</summary>
    public const string CancelledNotePrefix = "reversed in the open period by ";

    private const int ReversalPageSize = 500;

    private FinancialProjectionRound(FinancialLotPool pool, IReadOnlyList<AccountingLot> importedLots)
    {
        Pool = pool;
        ImportedLots = importedLots;
    }

    /// <summary>The open lots, kept in step with what the round stages.</summary>
    public FinancialLotPool Pool { get; }

    /// <summary>The lots of a lot import (D-A9), whatever is left of them.</summary>
    public IReadOnlyList<AccountingLot> ImportedLots { get; }

    /// <summary>The facts after the cursor whose reversal is after the cursor too (key → the reversal's key).</summary>
    public Dictionary<string, string> CancelledFacts { get; } = new(StringComparer.Ordinal);

    /// <summary>The financial lines of the cancelled facts projected in this round (key → lines).</summary>
    public Dictionary<string, IReadOnlyList<AccountingPosting>> CancelledLines { get; } = new(StringComparer.Ordinal);

    /// <summary>The ledger sequence to roll back to first (a reversal of a fact the book already projected in the
    /// open period), or null.</summary>
    public long? RollbackTo { get; private set; }

    /// <summary>Why <see cref="RollbackTo"/> is set.</summary>
    public string? RollbackReason { get; private set; }

    /// <summary>Whether the opening balances' lots were imported (D-A9).</summary>
    public bool HasImportedLots => ImportedLots.Count > 0;

    public static async Task<FinancialProjectionRound> LoadAsync(IUnitOfWork unitOfWork, long cursor,
                                                                 AccountingCostBasisMethod method, string currency,
                                                                 IAccountingAdjustmentSink sink,
                                                                 CancellationToken cancellationToken)
    {
        var lots = unitOfWork.AccountingLotDbRepository;
        var pool = new FinancialLotPool(await lots.ListOpenLotsAsync(cancellationToken: cancellationToken), method,
                                        currency);
        var imported = await lots.ListLotsByOriginAsync(AccountingLotOrigin.Import, cancellationToken);
        return await LoadAsync(unitOfWork, cursor, pool, imported, sink, cancellationToken);
    }

    /// <summary>A round over a pool kept from the round before (NL-658): only the reversals after the cursor are
    /// read.</summary>
    public static async Task<FinancialProjectionRound> LoadAsync(IUnitOfWork unitOfWork, long cursor,
                                                                 FinancialLotPool pool,
                                                                 IReadOnlyList<AccountingLot> importedLots,
                                                                 IAccountingAdjustmentSink sink,
                                                                 CancellationToken cancellationToken)
    {
        var round = new FinancialProjectionRound(pool, importedLots);
        await round.FindReversalsAsync(unitOfWork, cursor, sink, cancellationToken);
        return round;
    }

    /// <summary>
    /// The reversals after the cursor: a fact after the cursor too is cancelled with it (neither touches the lots); a
    /// fact the book already projected in the open period (not as cancelled) needs a rollback to it first; a fact of a
    /// closed period, or one the book does not hold, is left to the reversal's own entry.
    /// </summary>
    private async Task FindReversalsAsync(IUnitOfWork unitOfWork, long cursor, IAccountingAdjustmentSink sink,
                                          CancellationToken cancellationToken)
    {
        var events = unitOfWork.AccountingEventDbRepository;
        var books = unitOfWork.AccountingBooksDbRepository;
        var after = cursor;
        while (true)
        {
            var page = await events.ListAsync(new AccountingEventQuery(after, ReversalPageSize,
                                                                       [AccountingEventKind.Reversal]),
                                              cancellationToken);
            foreach (var reversal in page)
            {
                if (!reversal.Details.TryGetValue(AccountingConfirmations.ReversesDetail, out var target)
                 || string.IsNullOrEmpty(target))
                    continue;

                var original = await books.GetEntryByKeyAsync(target, cancellationToken);
                if (original is null)
                    continue;

                if (original.LedgerSeq > cursor)
                {
                    CancelledFacts[target] = reversal.EventKey;
                    continue;
                }

                var financial = await books.GetEntriesByKeyAsync(AccountingBook.Financial, target, cancellationToken);
                var projected = financial.FirstOrDefault(e => e.Adjustment == 0);
                if (projected is null || projected.ClosedPeriodId is not null
                                      || projected.Note?.Contains(CancelledNotePrefix, StringComparison.Ordinal) == true
                                      || await sink.GetLockingPeriodAsync(projected.OccurredAt, cancellationToken)
                                             is not null)
                    continue;

                if (RollbackTo is null || projected.LedgerSeq < RollbackTo)
                {
                    RollbackTo = projected.LedgerSeq;
                    RollbackReason = $"{reversal.EventKey} reverses {target}, projected in the open period";
                }
            }

            if (page.Count < ReversalPageSize)
                return;

            after = page[^1].LedgerSeq ?? after;
        }
    }
}

/// <summary>
/// The stored prices of a page of the financial projector (D-A11): one window query over the page's times, or one lookup
/// per time past <see cref="Cap"/> prices.
/// </summary>
internal sealed class FinancialPriceWindow
{
    public const int Cap = 20_000;

    private readonly IAccountingPriceDbRepository _prices;
    private readonly string _currency;
    private readonly TimeSpan _maxAge;
    private readonly IReadOnlyList<AccountingPrice> _window;
    private readonly bool _complete;

    private FinancialPriceWindow(IAccountingPriceDbRepository prices, string currency, TimeSpan maxAge,
                                 IReadOnlyList<AccountingPrice> window, bool complete)
    {
        _prices = prices;
        _currency = currency;
        _maxAge = maxAge;
        _window = window;
        _complete = complete;
    }

    public static async Task<FinancialPriceWindow> LoadAsync(IAccountingPriceDbRepository prices, string currency,
                                                             TimeSpan maxAge, IReadOnlyCollection<DateTimeOffset> times,
                                                             CancellationToken cancellationToken)
    {
        if (times.Count == 0)
            return new FinancialPriceWindow(prices, currency, maxAge, [], true);

        var earliest = times.Min();
        var latest = times.Max();
        DateTimeOffset? since = earliest - DateTimeOffset.MinValue > maxAge ? earliest - maxAge : null;
        var window = await prices.ListAsync(currency, since, latest.AddTicks(1), Cap, cancellationToken);
        return new FinancialPriceWindow(prices, currency, maxAge, window, window.Count < Cap);
    }

    /// <summary>The nearest stored price at or before <paramref name="at"/> within the maximum age, or null.</summary>
    public async Task<AccountingPrice?> FindAsync(DateTimeOffset at, CancellationToken cancellationToken) =>
        _complete
            ? AccountingValuation.NearestAtOrBefore(_window, p => p.Time, at, _maxAge)
            : await _prices.GetAtOrBeforeAsync(_currency, at, _maxAge, cancellationToken);
}