using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Accounting.Export.Financial;

using Domain.Accounting.Books;
using Domain.Accounting.Financial;
using Domain.Accounting.Financial.Export;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Persistence.Interfaces;
using Reports.Financial;

/// <summary>
/// Exports the financial book page by page (NL-602 A3-T6, plan §6.2, §11): the text goes back to the caller (the IPC
/// handler streams it to the client); the daemon never writes a file. The formats are
/// <see cref="AccountingFinancialExportFormatter"/>'s.
/// </summary>
/// <remarks>
/// A page is the financial entries after a (ledger sequence, adjustment) cursor that occurred in the period, joined with
/// the feed's events of their sequences (descriptions). The first page starts with the header; for the journals it
/// scans the period once first (the accounts, the earliest date, the prices to declare): an on-demand export, never on a
/// hot path.
/// </remarks>
public sealed class AccountingFinancialExportService : IAccountingFinancialExports
{
    /// <summary>The largest page.</summary>
    public const int MaxTake = 1_000;

    // How far before the first entry the header looks for the prices it declares (a posting takes the nearest price at
    // or before it within Accounting:Prices:MaxAge, 26 h by default); a price outside is read by its id
    private static readonly TimeSpan s_priceLookback = TimeSpan.FromDays(7);

    private readonly AccountingFinancialAccess _access;
    private readonly IServiceScopeFactory _scopeFactory;

    public AccountingFinancialExportService(IServiceScopeFactory scopeFactory, IAccountingBooks? books,
                                            ILogger<AccountingFinancialExportService> logger,
                                            IAccountingEventSealer? sealer = null,
                                            IAccountingFinancialProjection? projection = null)
    {
        _scopeFactory = scopeFactory;
        _access = new AccountingFinancialAccess(books, sealer, projection, logger);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">A negative cursor, a page outside 1 to <see cref="MaxTake"/>, an empty
    /// period, an unknown format or a bad currency.</exception>
    public async Task<AccountingFinancialExportChunk> ExportAsync(AccountingFinancialExportQuery query,
                                                                  CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegative(query.AfterLedgerSeq);
        if (query.AfterAdjustment is < 0)
            throw new ArgumentOutOfRangeException(nameof(query), query.AfterAdjustment,
                                                  "The cursor's adjustment must not be negative.");
        if (query.Take is <= 0 or > MaxTake)
            throw new ArgumentOutOfRangeException(nameof(query), query.Take,
                                                  $"The page must hold 1 to {MaxTake} entries.");
        if (query.Since is { } since && query.Until is { } until && until <= since)
            throw new ArgumentException("until must be after since.", nameof(query));

        var formatter = AccountingFinancialExportFormatter.For(query.Format, query.Currency ?? string.Empty);
        await _access.PrepareAsync(cancellationToken);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var books = unitOfWork.AccountingBooksDbRepository;
        var builder = new StringBuilder();
        if (query.IsFirstPage)
        {
            var header = AccountingFinancialExportHeader.Empty;
            if (formatter.NeedsHeaderScan)
                header = await ScanAsync(unitOfWork, query, formatter.Currency, cancellationToken);
            formatter.WriteHeader(builder, header);
        }

        var entries = await books.ListEntriesAsync(
                          new AccountingEntryQuery(query.AfterLedgerSeq, query.Take, query.Since, query.Until)
                          {
                              Book = AccountingBook.Financial,
                              AfterAdjustment = query.AfterAdjustment ?? int.MaxValue
                          }, cancellationToken);
        var events = await LoadEventsAsync(unitOfWork.AccountingEventDbRepository, entries, cancellationToken);
        var prices = await LoadPricesAsync(unitOfWork.AccountingPriceDbRepository,
                                           entries.SelectMany(e => e.Postings)
                                                  .Select(p => p.PriceId)
                                                  .OfType<long>()
                                                  .ToHashSet(), cancellationToken);
        foreach (var entry in entries)
            formatter.WriteEntry(builder, new AccountingFinancialExportItem(
                                     entry, events.GetValueOrDefault(entry.LedgerSeq), prices));

        return entries.Count == 0
                   ? new AccountingFinancialExportChunk(builder.ToString(), query.AfterLedgerSeq, query.AfterAdjustment,
                                                        false, 0)
                   : new AccountingFinancialExportChunk(builder.ToString(), entries[^1].LedgerSeq,
                                                        entries[^1].Adjustment, entries.Count == query.Take,
                                                        entries.Count);
    }

    /// <summary>
    /// The feed's events of the entries' ledger sequences (for the descriptions), read in pages over the range the
    /// entries span; a sequence the pages do not reach gets no description.
    /// </summary>
    internal static async Task<Dictionary<long, AccountingEventModel>> LoadEventsAsync(
        IAccountingEventDbRepository events, IReadOnlyList<AccountingEntry> entries,
        CancellationToken cancellationToken)
    {
        var wanted = entries.Select(e => e.LedgerSeq).ToHashSet();
        var found = new Dictionary<long, AccountingEventModel>();
        if (wanted.Count == 0)
            return found;

        var after = wanted.Min() - 1;
        var last = wanted.Max();
        for (var page = 0; page < 20 && found.Count < wanted.Count && after < last; page++)
        {
            var batch = await events.ListAsync(new AccountingEventQuery(after, MaxTake), cancellationToken);
            foreach (var accountingEvent in batch)
                if (accountingEvent.LedgerSeq is { } seq && wanted.Contains(seq))
                    found.TryAdd(seq, accountingEvent);

            if (batch.Count < MaxTake || batch[^1].LedgerSeq is not { } next || next <= after)
                break;

            after = next;
        }

        return found;
    }

    /// <summary>The stored prices of these ids.</summary>
    internal static async Task<Dictionary<long, AccountingPrice>> LoadPricesAsync(
        IAccountingPriceDbRepository prices, IReadOnlyCollection<long> ids, CancellationToken cancellationToken)
    {
        var found = new Dictionary<long, AccountingPrice>();
        foreach (var id in ids.Order())
            if (await prices.GetByIdAsync(id, cancellationToken) is { } price)
                found[id] = price;

        return found;
    }

    // The header of the journals: one pass over the period's entries (the accounts, the earliest date, the prices)
    private static async Task<AccountingFinancialExportHeader> ScanAsync(IUnitOfWork unitOfWork,
                                                                         AccountingFinancialExportQuery query,
                                                                         string currency,
                                                                         CancellationToken cancellationToken)
    {
        var books = unitOfWork.AccountingBooksDbRepository;
        var builder = new AccountingFinancialExportHeaderBuilder(currency);
        DateTimeOffset? lastDate = null;
        var afterSeq = 0L;
        var afterAdjustment = int.MaxValue;
        while (true)
        {
            var page = await books.ListEntriesAsync(new AccountingEntryQuery(afterSeq, MaxTake, query.Since,
                                                                             query.Until)
            {
                Book = AccountingBook.Financial,
                AfterAdjustment = afterAdjustment
            }, cancellationToken);
            foreach (var entry in page)
            {
                builder.Add(entry);
                if (lastDate is null || entry.OccurredAt > lastDate)
                    lastDate = entry.OccurredAt;
            }

            if (page.Count < MaxTake)
                break;

            (afterSeq, afterAdjustment) = (page[^1].LedgerSeq, page[^1].Adjustment);
        }

        var prices = new Dictionary<long, AccountingPrice>();
        if (builder.PriceIds.Count > 0 && builder.FirstDate is { } first && lastDate is { } end)
        {
            // The prices around the period in one read, the others by id
            foreach (var price in await unitOfWork.AccountingPriceDbRepository.ListAsync(
                                      currency, first - s_priceLookback, end.AddTicks(1), 100_000, cancellationToken))
                if (builder.PriceIds.Contains(price.Id))
                    prices[price.Id] = price;

            var missing = builder.PriceIds.Where(id => !prices.ContainsKey(id)).ToList();
            foreach (var (id, price) in await LoadPricesAsync(unitOfWork.AccountingPriceDbRepository, missing,
                                                              cancellationToken))
                prices[id] = price;
        }

        return builder.Build(prices.Values);
    }
}