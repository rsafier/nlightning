using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Accounting;

using Domain.Accounting.Books;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Persistence.Contexts;
using Persistence.Entities.Accounting;

/// <summary>
/// The books' tables (NL-602 A2, plan §6.3; per book since migration <c>AddAccountingFinancial</c>, A3-T0): entries,
/// postings, the running balance per account and the projectors' cursors, one row per book. Writes are staged on the
/// unit of work and committed by its save, so a projector's cursor and the entries it produced commit together
/// (exactly-once).
/// </summary>
/// <remarks>
/// <para>The members without a book argument are the operational book's (A2): every read filters on it, so the
/// financial book's rows never leak into the operational reports, reconcile or rebuild.</para>
/// <para><see cref="ClearAsync(AccountingBook, CancellationToken)"/> is the exception to staging: it deletes the book's
/// rows at once (bulk deletes, which the change tracker does not see) and forgets the book's rows this unit of work
/// tracks. It deletes the book's cursor too, so a crash between the clear and the next save leaves that book empty at
/// cursor 0, which its projector rebuilds from the start.</para>
/// </remarks>
public class AccountingBooksDbRepository : IAccountingBooksDbRepository
{
    private const byte OperationalBook = (byte)AccountingBook.Operational;

    private readonly NLightningDbContext _context;

    public AccountingBooksDbRepository(NLightningDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <inheritdoc />
    public Task<long> GetCursorAsync(CancellationToken cancellationToken = default) =>
        GetCursorAsync(AccountingBook.Operational, cancellationToken);

    /// <inheritdoc />
    public async Task<long> GetCursorAsync(AccountingBook book, CancellationToken cancellationToken = default)
    {
        var bookValue = (byte)book;
        var tracked = _context.AccountingCursor.Local.FirstOrDefault(c => c.Book == bookValue);
        if (tracked is not null)
            return tracked.LastLedgerSeq;

        return await _context.AccountingCursor.AsNoTracking()
                             .Where(c => c.Book == bookValue)
                             .Select(c => c.LastLedgerSeq)
                             .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task SetCursorAsync(long ledgerSeq, CancellationToken cancellationToken = default) =>
        SetCursorAsync(AccountingBook.Operational, ledgerSeq, cancellationToken);

    /// <inheritdoc />
    public async Task SetCursorAsync(AccountingBook book, long ledgerSeq,
                                     CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ledgerSeq);

        var bookValue = (byte)book;
        var cursor = await _context.AccountingCursor.FindAsync([bookValue], cancellationToken);
        if (cursor is null)
        {
            _context.AccountingCursor.Add(new AccountingCursorEntity
            {
                Book = bookValue,
                LastLedgerSeq = ledgerSeq
            });
            return;
        }

        cursor.LastLedgerSeq = ledgerSeq;
    }

    /// <inheritdoc />
    public async Task AddEntryAsync(AccountingEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(entry.LedgerSeq);
        ArgumentOutOfRangeException.ThrowIfNegative(entry.Adjustment);
        ArgumentException.ThrowIfNullOrEmpty(entry.EventKey);
        ArgumentNullException.ThrowIfNull(entry.Postings);
        if (!entry.IsBalanced)
            throw new ArgumentException(
                $"The entry of {entry.EventKey} does not balance ({entry.Postings.Sum(p => p.AmountMsat)} msat)",
                nameof(entry));
        ValidatePostings(entry);

        var book = (byte)entry.Book;
        _context.AccountingEntries.Add(new AccountingEntryEntity
        {
            Book = book,
            LedgerSeq = entry.LedgerSeq,
            Adjustment = entry.Adjustment,
            EventKey = entry.EventKey,
            Kind = (int)entry.Kind,
            OccurredAt = entry.OccurredAt,
            ChannelId = entry.ChannelId,
            PaymentHash = entry.PaymentHash,
            Note = entry.Note,
            Flags = (int)entry.Flags,
            Classification = entry.Classification is { } classification ? (byte)classification : null,
            RuleId = entry.RuleId,
            ClosedPeriodId = entry.ClosedPeriodId
        });

        for (var index = 0; index < entry.Postings.Count; index++)
        {
            var posting = entry.Postings[index];
            _context.AccountingPostings.Add(new AccountingPostingEntity
            {
                Book = book,
                LedgerSeq = entry.LedgerSeq,
                Adjustment = entry.Adjustment,
                Index = index,
                Account = (int)posting.Account,
                AccountName = posting.AccountName,
                AmountMsat = posting.AmountMsat,
                OccurredAt = entry.OccurredAt,
                FiatAmount = posting.FiatAmount,
                FiatCurrency = posting.FiatCurrency,
                PriceId = posting.PriceId
            });
        }

        foreach (var group in entry.Postings.GroupBy(p => (p.Account, Name: p.AccountName ?? string.Empty)))
        {
            var balance = await FindOrAddBalanceAsync(book, (int)group.Key.Account, group.Key.Name, cancellationToken);
            balance.BalanceMsat = checked(balance.BalanceMsat + group.Sum(p => p.AmountMsat));
            balance.FiatAmount += group.Sum(p => p.FiatAmount ?? 0m);
        }
    }

    /// <inheritdoc />
    public async Task<AccountingEntry?> GetEntryByKeyAsync(string eventKey,
                                                           CancellationToken cancellationToken = default)
    {
        var entries = await GetEntriesByKeyAsync(AccountingBook.Operational, eventKey, cancellationToken);
        return entries.FirstOrDefault(e => e.Adjustment == 0);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountingEntry>> GetEntriesByKeyAsync(
        AccountingBook book, string eventKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventKey);

        var bookValue = (byte)book;

        // Saved rows, then the ones staged in this unit of work (Local holds no deleted rows; a staged row wins)
        var entities = await _context.AccountingEntries.AsNoTracking()
                                     .Where(e => e.Book == bookValue && e.EventKey == eventKey)
                                     .ToListAsync(cancellationToken);
        var byAdjustment = entities.ToDictionary(e => e.Adjustment);
        foreach (var staged in _context.AccountingEntries.Local.Where(e => e.Book == bookValue
                                                                        && e.EventKey == eventKey))
            byAdjustment[staged.Adjustment] = staged;

        if (byAdjustment.Count == 0)
            return [];

        var ledgerSeq = byAdjustment.Values.First().LedgerSeq;
        var postings = await _context.AccountingPostings.AsNoTracking()
                                     .Where(p => p.Book == bookValue && p.LedgerSeq == ledgerSeq)
                                     .ToListAsync(cancellationToken);
        var postingsByKey = postings.ToDictionary(p => (p.Adjustment, p.Index));
        foreach (var staged in _context.AccountingPostings.Local.Where(p => p.Book == bookValue
                                                                         && p.LedgerSeq == ledgerSeq))
            postingsByKey[(staged.Adjustment, staged.Index)] = staged;

        var postingsByAdjustment = postingsByKey.Values
                                                .GroupBy(p => p.Adjustment)
                                                .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Index).ToList());

        return byAdjustment.Values
                           .OrderBy(e => e.Adjustment)
                           .Select(e => MapEntityToDomain(e, postingsByAdjustment.GetValueOrDefault(e.Adjustment) ?? []))
                           .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<AccountRole, long>> GetBalancesAsync(
        CancellationToken cancellationToken = default)
    {
        var balances = await GetAccountBalancesAsync(AccountingBook.Operational, cancellationToken);
        return balances.GroupBy(b => b.Account).ToDictionary(g => g.Key, g => g.Sum(b => b.BalanceMsat));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountingAccountBalance>> GetAccountBalancesAsync(
        AccountingBook book, CancellationToken cancellationToken = default)
    {
        var bookValue = (byte)book;
        var saved = await _context.AccountingBalances.AsNoTracking()
                                  .Where(b => b.Book == bookValue)
                                  .ToListAsync(cancellationToken);
        var balances = saved.ToDictionary(b => (b.Account, b.AccountName));

        // What this unit of work staged wins over what is saved
        foreach (var tracked in _context.AccountingBalances.Local.Where(b => b.Book == bookValue))
            balances[(tracked.Account, tracked.AccountName)] = tracked;

        return balances.Values
                       .OrderBy(b => b.Account)
                       .ThenBy(b => b.AccountName, StringComparer.Ordinal)
                       .Select(b => new AccountingAccountBalance(book, (AccountRole)b.Account,
                                                                 b.AccountName.Length == 0 ? null : b.AccountName,
                                                                 b.BalanceMsat, b.FiatAmount))
                       .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<AccountRole, long>> SumPostingsAsync(
        DateTimeOffset? since, DateTimeOffset? until, CancellationToken cancellationToken = default)
    {
        var postings = _context.AccountingPostings.AsNoTracking().Where(p => p.Book == OperationalBook);
        if (since is { } from)
            postings = postings.Where(p => p.OccurredAt >= from);
        if (until is { } to)
            postings = postings.Where(p => p.OccurredAt < to);

        var sums = await postings.GroupBy(p => p.Account)
                                 .Select(g => new { Account = g.Key, Sum = g.Sum(p => p.AmountMsat) })
                                 .ToListAsync(cancellationToken);
        return sums.ToDictionary(s => (AccountRole)s.Account, s => s.Sum);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountingEntry>> ListEntriesAsync(AccountingEntryQuery query,
                                                                       CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.Take);

        var book = (byte)query.Book;
        var afterSeq = query.AfterLedgerSeq;
        var afterAdjustment = query.AfterAdjustment;
        var entries = _context.AccountingEntries.AsNoTracking().Where(e => e.Book == book);
        entries = afterAdjustment == int.MaxValue
                      ? entries.Where(e => e.LedgerSeq > afterSeq)
                      : entries.Where(e => e.LedgerSeq > afterSeq
                                        || (e.LedgerSeq == afterSeq && e.Adjustment > afterAdjustment));
        if (query.Since is { } since)
            entries = entries.Where(e => e.OccurredAt >= since);

        if (query.Until is { } until)
            entries = entries.Where(e => e.OccurredAt < until);

        if (query.Kinds is { Count: > 0 } kinds)
        {
            var kindValues = kinds.Select(k => (int)k).ToList();
            entries = entries.Where(e => kindValues.Contains(e.Kind));
        }

        if (query.ChannelId is { } channelId)
            entries = entries.Where(e => e.ChannelId == channelId);

        if (query.ClosedPeriodId is { } periodId)
            entries = entries.Where(e => e.ClosedPeriodId == periodId);

        if (query.WithFlags != AccountingEntryFlags.None)
        {
            var flags = (int)query.WithFlags;
            entries = entries.Where(e => (e.Flags & flags) == flags);
        }

        if (query.Account is { } accountRole)
        {
            var account = (int)accountRole;
            entries = entries.Where(e => _context.AccountingPostings.Any(p => p.Book == e.Book
                                                                           && p.LedgerSeq == e.LedgerSeq
                                                                           && p.Adjustment == e.Adjustment
                                                                           && p.Account == account));
        }

        var entities = await entries.OrderBy(e => e.LedgerSeq)
                                    .ThenBy(e => e.Adjustment)
                                    .Take(query.Take)
                                    .ToListAsync(cancellationToken);
        if (entities.Count == 0)
            return [];

        var ledgerSeqs = entities.Select(e => e.LedgerSeq).Distinct().ToList();
        var postings = await _context.AccountingPostings.AsNoTracking()
                                     .Where(p => p.Book == book && ledgerSeqs.Contains(p.LedgerSeq))
                                     .ToListAsync(cancellationToken);
        var postingsByEntry = postings.GroupBy(p => (p.LedgerSeq, p.Adjustment))
                                      .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Index).ToList());
        return entities
              .Select(e => MapEntityToDomain(e, postingsByEntry.GetValueOrDefault((e.LedgerSeq, e.Adjustment)) ?? []))
              .ToList();
    }

    /// <inheritdoc />
    public async Task<bool> SetPostingValueAsync(AccountingPostingKey posting, decimal fiatAmount,
                                                 string fiatCurrency, long priceId,
                                                 CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fiatCurrency);

        var entity = await _context.AccountingPostings.FindAsync(
                         [(byte)posting.Book, posting.LedgerSeq, posting.Adjustment, posting.Index],
                         cancellationToken);
        if (entity is null || entity.PriceId is not null || entity.FiatAmount is not null)
            return false;

        entity.FiatAmount = fiatAmount;
        entity.FiatCurrency = fiatCurrency;
        entity.PriceId = priceId;

        var balance = await FindOrAddBalanceAsync(entity.Book, entity.Account, entity.AccountName ?? string.Empty,
                                                  cancellationToken);
        balance.FiatAmount += fiatAmount;

        // The entry is valued once none of its lines is left without a value (what this unit of work staged
        // included: a tracked query returns the tracked instances as they are)
        var lines = await _context.AccountingPostings
                                  .Where(p => p.Book == entity.Book && p.LedgerSeq == entity.LedgerSeq
                                           && p.Adjustment == entity.Adjustment)
                                  .ToListAsync(cancellationToken);
        if (lines.All(p => p.FiatAmount is not null))
        {
            var entry = await _context.AccountingEntries.FindAsync(
                            [entity.Book, entity.LedgerSeq, entity.Adjustment], cancellationToken);
            if (entry is not null && (entry.Flags & (int)AccountingEntryFlags.Unvalued) != 0)
                entry.Flags &= ~(int)AccountingEntryFlags.Unvalued;
        }

        return true;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AccountingUnvaluedPosting>> ListUnvaluedPostingsAsync(
        AccountingBook book, int take, CancellationToken cancellationToken = default) =>
        ListUnvaluedPostingsAsync(book, null, take, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountingUnvaluedPosting>> ListUnvaluedPostingsAsync(
        AccountingBook book, AccountingUnvaluedPostingCursor? after, int take,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);

        var bookValue = (byte)book;
        var postings = _context.AccountingPostings.AsNoTracking()
                               .Where(p => p.Book == bookValue && p.PriceId == null && p.FiatAmount == null);
        if (after is { } cursor)
        {
            // Keyset paging in the list's order (time, ledger sequence, adjustment, line)
            var time = cursor.OccurredAt;
            var seq = cursor.LedgerSeq;
            var adjustment = cursor.Adjustment;
            var index = cursor.Index;
            postings = postings.Where(p => p.OccurredAt > time
                                        || (p.OccurredAt == time
                                         && (p.LedgerSeq > seq
                                          || (p.LedgerSeq == seq
                                           && (p.Adjustment > adjustment
                                            || (p.Adjustment == adjustment && p.Index > index))))));
        }

        var rows = await (from p in postings
                          join e in _context.AccountingEntries.AsNoTracking()
                              on new { p.Book, p.LedgerSeq, p.Adjustment }
                              equals new { e.Book, e.LedgerSeq, e.Adjustment }
                          orderby p.OccurredAt, p.LedgerSeq, p.Adjustment, p.Index
                          select new { Posting = p, e.ClosedPeriodId })
                        .Take(take)
                        .ToListAsync(cancellationToken);

        return rows.Select(r => new AccountingUnvaluedPosting(
                                new AccountingPostingKey(book, r.Posting.LedgerSeq, r.Posting.Adjustment,
                                                         r.Posting.Index),
                                r.Posting.OccurredAt, (AccountRole)r.Posting.Account, r.Posting.AccountName,
                                r.Posting.AmountMsat, r.ClosedPeriodId))
                   .ToList();
    }

    /// <inheritdoc />
    public async Task<int> MarkEntriesClosedAsync(AccountingBook book, string periodId, DateTimeOffset start,
                                                  DateTimeOffset end, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(periodId);

        var bookValue = (byte)book;
        var entries = await _context.AccountingEntries
                                    .Where(e => e.Book == bookValue && e.ClosedPeriodId == null
                                             && e.OccurredAt >= start && e.OccurredAt < end)
                                    .ToListAsync(cancellationToken);
        foreach (var entry in entries)
            entry.ClosedPeriodId = periodId;

        return entries.Count;
    }

    /// <inheritdoc />
    /// <remarks>Runs at once (see the class remarks), not at the unit of work's save.</remarks>
    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        ClearAsync(AccountingBook.Operational, cancellationToken);

    /// <inheritdoc />
    /// <remarks>Runs at once (see the class remarks), not at the unit of work's save.</remarks>
    public async Task ClearAsync(AccountingBook book, CancellationToken cancellationToken = default)
    {
        var bookValue = (byte)book;

        // The bulk deletes bypass the change tracker: forget what it tracks of the book, or the next save would write
        // rows that no longer exist
        foreach (var tracked in _context.ChangeTracker.Entries()
                                        .Where(e => e.Entity switch
                                         {
                                             AccountingEntryEntity entry => entry.Book == bookValue,
                                             AccountingPostingEntity posting => posting.Book == bookValue,
                                             AccountingBalanceEntity balance => balance.Book == bookValue,
                                             AccountingCursorEntity cursor => cursor.Book == bookValue,
                                             _ => false
                                         })
                                        .ToList())
            tracked.State = EntityState.Detached;

        await _context.AccountingPostings.Where(p => p.Book == bookValue).ExecuteDeleteAsync(cancellationToken);
        await _context.AccountingEntries.Where(e => e.Book == bookValue).ExecuteDeleteAsync(cancellationToken);
        await _context.AccountingBalances.Where(b => b.Book == bookValue).ExecuteDeleteAsync(cancellationToken);
        await _context.AccountingCursor.Where(c => c.Book == bookValue).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// The column limits and the books' contract: a financial line names its account (the financial chart's, a rule's
    /// or an override's target), an operational line never does (its name comes from the role); a fiat amount comes
    /// with its currency.
    /// </summary>
    private static void ValidatePostings(AccountingEntry entry)
    {
        if (entry.ClosedPeriodId is { Length: > AccountingSchemaLimits.PeriodIdMaxLength })
            throw new ArgumentException($"Period id '{entry.ClosedPeriodId}' is too long", nameof(entry));

        foreach (var posting in entry.Postings)
        {
            var operational = entry.Book == AccountingBook.Operational;
            if (operational ? posting.AccountName is not null : string.IsNullOrWhiteSpace(posting.AccountName))
                throw new ArgumentException(
                    operational
                        ? $"An operational line of {entry.EventKey} takes its name from its role"
                        : $"A {entry.Book} line of {entry.EventKey} must name its account", nameof(entry));

            if (posting.AccountName is { Length: > AccountingSchemaLimits.AccountNameMaxLength })
                throw new ArgumentException($"The account name of a line of {entry.EventKey} is too long",
                                            nameof(entry));

            if ((posting.FiatAmount is null) != (posting.FiatCurrency is null)
             || posting.FiatCurrency is { Length: not AccountingSchemaLimits.CurrencyLength })
                throw new ArgumentException(
                    $"A valued line of {entry.EventKey} needs its amount and a three-letter currency", nameof(entry));
        }
    }

    private async Task<AccountingBalanceEntity> FindOrAddBalanceAsync(byte book, int account, string accountName,
                                                                      CancellationToken cancellationToken)
    {
        var balance = await _context.AccountingBalances.FindAsync([book, account, accountName], cancellationToken);
        if (balance is not null)
            return balance;

        balance = new AccountingBalanceEntity { Book = book, Account = account, AccountName = accountName };
        _context.AccountingBalances.Add(balance);
        return balance;
    }

    private static AccountingEntry MapEntityToDomain(AccountingEntryEntity entity,
                                                     IEnumerable<AccountingPostingEntity> postings) =>
        new(entity.LedgerSeq, entity.EventKey, (AccountingEventKind)entity.Kind, entity.OccurredAt, entity.ChannelId,
            entity.PaymentHash,
            postings.Select(p => new AccountingPosting((AccountRole)p.Account, p.AmountMsat)
            {
                AccountName = p.AccountName,
                FiatAmount = p.FiatAmount,
                FiatCurrency = p.FiatCurrency,
                PriceId = p.PriceId
            })
                    .ToList(), entity.Note)
        {
            Book = (AccountingBook)entity.Book,
            Adjustment = entity.Adjustment,
            Flags = (AccountingEntryFlags)entity.Flags,
            Classification = entity.Classification is { } classification
                                 ? (AccountingClassificationSource)classification
                                 : null,
            RuleId = entity.RuleId,
            ClosedPeriodId = entity.ClosedPeriodId
        };
}