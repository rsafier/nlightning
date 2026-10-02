using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Accounting.Export;

using Domain.Accounting.Books;
using Domain.Accounting.Books.Export;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Persistence.Interfaces;
using Reports;

/// <summary>
/// Exports the books page by page (plan §6.1, §11): the text is returned to the caller (the IPC handler streams it to
/// the client), the daemon never writes a file.
/// </summary>
/// <remarks>
/// A page is the entries after a ledger sequence that occurred in the period, in ledger order, joined with their
/// events (same sequence) for the descriptions. The first page (cursor 0) starts with the format's header; for
/// beancount that needs the earliest entry's date and the accounts in use, so the first page reads the period once
/// before it writes (an on-demand export, never on a hot path).
/// </remarks>
public sealed class AccountingExportService : IAccountingExports
{
    /// <summary>The largest page.</summary>
    public const int MaxTake = 1_000;

    private readonly AccountingBooksAccess _access;
    private readonly AccountNames _names;
    private readonly IServiceScopeFactory _scopeFactory;

    public AccountingExportService(IServiceScopeFactory scopeFactory, IAccountingBooks? books,
                                   ILogger<AccountingExportService> logger,
                                   IOptions<AccountingOptions>? options = null,
                                   IAccountingEventSealer? sealer = null)
    {
        _scopeFactory = scopeFactory;
        _access = new AccountingBooksAccess(books, sealer, logger);
        _names = (options?.Value ?? new AccountingOptions()).GetAccountNames();
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">A negative cursor, a page outside 1 to <see cref="MaxTake"/>, an empty
    /// period or an unknown format.</exception>
    public async Task<AccountingExportChunk> ExportAsync(AccountingExportQuery query,
                                                         CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegative(query.AfterLedgerSeq);
        if (query.Take is <= 0 or > MaxTake)
            throw new ArgumentOutOfRangeException(nameof(query), query.Take,
                                                  $"The page must hold 1 to {MaxTake} entries.");
        if (query.Since is { } since && query.Until is { } until && until <= since)
            throw new ArgumentException("until must be after since.", nameof(query));

        var formatter = AccountingExportFormatter.For(query.Format, _names);
        await _access.PrepareAsync(cancellationToken);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var books = unitOfWork.AccountingBooksDbRepository;
        var builder = new StringBuilder();
        if (query.AfterLedgerSeq == 0)
        {
            DateTimeOffset? firstDate = null;
            IReadOnlyCollection<AccountRole> accounts = [];
            if (formatter.NeedsHeaderScan)
                (firstDate, accounts) = await ScanAsync(books, query, cancellationToken);
            formatter.WriteHeader(builder, firstDate, accounts);
        }

        var entries = await books.ListEntriesAsync(
                          new AccountingEntryQuery(query.AfterLedgerSeq, query.Take, query.Since, query.Until),
                          cancellationToken);
        var events = entries.Count == 0
                         ? []
                         : await unitOfWork.AccountingEventDbRepository.ListAsync(
                               new AccountingEventQuery(query.AfterLedgerSeq, query.Take, null, null, query.Since,
                                                        query.Until), cancellationToken);
        var eventsBySeq = new Dictionary<long, AccountingEventModel>();
        foreach (var accountingEvent in events)
            if (accountingEvent.LedgerSeq is { } seq)
                eventsBySeq.TryAdd(seq, accountingEvent);

        foreach (var entry in entries)
            formatter.WriteEntry(builder, new AccountingExportItem(entry, eventsBySeq.GetValueOrDefault(entry.LedgerSeq)));

        var nextAfter = entries.Count > 0 ? entries[^1].LedgerSeq : query.AfterLedgerSeq;
        return new AccountingExportChunk(builder.ToString(), nextAfter, entries.Count == query.Take, entries.Count);
    }

    // The earliest date and the accounts posted to in the period (beancount opens every account it uses before it)
    private static async Task<(DateTimeOffset? FirstDate, IReadOnlyCollection<AccountRole> Accounts)> ScanAsync(
        IAccountingBooksDbRepository books, AccountingExportQuery query, CancellationToken cancellationToken)
    {
        DateTimeOffset? firstDate = null;
        var accounts = new HashSet<AccountRole>();
        var after = 0L;
        while (true)
        {
            var page = await books.ListEntriesAsync(new AccountingEntryQuery(after, MaxTake, query.Since, query.Until),
                                                    cancellationToken);
            foreach (var entry in page)
            {
                if (entry.Postings.Count == 0)
                    continue;

                if (firstDate is null || entry.OccurredAt < firstDate)
                    firstDate = entry.OccurredAt;
                foreach (var posting in entry.Postings)
                    accounts.Add(posting.Account);
            }

            if (page.Count < MaxTake || page[^1].LedgerSeq <= after)
                return (firstDate, accounts);

            after = page[^1].LedgerSeq;
        }
    }
}