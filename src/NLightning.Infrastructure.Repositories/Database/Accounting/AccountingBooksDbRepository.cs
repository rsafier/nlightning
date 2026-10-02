using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Accounting;

using Domain.Accounting.Books;
using Domain.Accounting.Enums;
using Persistence.Contexts;
using Persistence.Entities.Accounting;

/// <summary>
/// The operational books' tables (NL-602 A2, plan §6.3): entries, postings, the running balance per account and the
/// projector's cursor. Writes are staged on the unit of work and committed by its save, so the projector's cursor and
/// the entries it produced commit together (exactly-once).
/// </summary>
/// <remarks>
/// <see cref="ClearAsync"/> is the exception: it deletes the rows at once (bulk deletes, which the change tracker does
/// not see) and forgets the books' rows this unit of work tracks. It deletes the cursor too, so a crash between the
/// clear and the next save leaves empty books at cursor 0, which the projector rebuilds from the start.
/// </remarks>
public class AccountingBooksDbRepository : IAccountingBooksDbRepository
{
    private readonly NLightningDbContext _context;

    public AccountingBooksDbRepository(NLightningDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <inheritdoc />
    public async Task<long> GetCursorAsync(CancellationToken cancellationToken = default)
    {
        var tracked = _context.AccountingCursor.Local.FirstOrDefault(c => c.Id == AccountingCursorEntity.SingletonId);
        if (tracked is not null)
            return tracked.LastLedgerSeq;

        return await _context.AccountingCursor.AsNoTracking()
                             .Where(c => c.Id == AccountingCursorEntity.SingletonId)
                             .Select(c => c.LastLedgerSeq)
                             .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetCursorAsync(long ledgerSeq, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ledgerSeq);

        var cursor = await _context.AccountingCursor.FindAsync([AccountingCursorEntity.SingletonId],
                                                               cancellationToken);
        if (cursor is null)
        {
            _context.AccountingCursor.Add(new AccountingCursorEntity
            {
                Id = AccountingCursorEntity.SingletonId,
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
        ArgumentException.ThrowIfNullOrEmpty(entry.EventKey);
        ArgumentNullException.ThrowIfNull(entry.Postings);
        if (!entry.IsBalanced)
            throw new ArgumentException(
                $"The entry of {entry.EventKey} does not balance ({entry.Postings.Sum(p => p.AmountMsat)} msat)",
                nameof(entry));

        _context.AccountingEntries.Add(new AccountingEntryEntity
        {
            LedgerSeq = entry.LedgerSeq,
            EventKey = entry.EventKey,
            Kind = (int)entry.Kind,
            OccurredAt = entry.OccurredAt,
            ChannelId = entry.ChannelId,
            PaymentHash = entry.PaymentHash,
            Note = entry.Note
        });

        for (var index = 0; index < entry.Postings.Count; index++)
        {
            var posting = entry.Postings[index];
            _context.AccountingPostings.Add(new AccountingPostingEntity
            {
                LedgerSeq = entry.LedgerSeq,
                Index = index,
                Account = (int)posting.Account,
                AmountMsat = posting.AmountMsat,
                OccurredAt = entry.OccurredAt
            });
        }

        foreach (var group in entry.Postings.GroupBy(p => p.Account))
        {
            var account = (int)group.Key;
            var balance = await _context.AccountingBalances.FindAsync([account], cancellationToken);
            if (balance is null)
            {
                balance = new AccountingBalanceEntity { Account = account };
                _context.AccountingBalances.Add(balance);
            }

            balance.BalanceMsat = checked(balance.BalanceMsat + group.Sum(p => p.AmountMsat));
        }
    }

    /// <inheritdoc />
    public async Task<AccountingEntry?> GetEntryByKeyAsync(string eventKey,
                                                           CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventKey);

        // Staged in this unit of work first (Local holds no deleted rows)
        var staged = _context.AccountingEntries.Local.FirstOrDefault(e => e.EventKey == eventKey);
        if (staged is not null)
        {
            var stagedPostings = _context.AccountingPostings.Local.Where(p => p.LedgerSeq == staged.LedgerSeq)
                                         .OrderBy(p => p.Index)
                                         .ToList();
            return MapEntityToDomain(staged, stagedPostings);
        }

        var entity = await _context.AccountingEntries.AsNoTracking()
                                   .FirstOrDefaultAsync(e => e.EventKey == eventKey, cancellationToken);
        if (entity is null)
            return null;

        var postings = await _context.AccountingPostings.AsNoTracking()
                                     .Where(p => p.LedgerSeq == entity.LedgerSeq)
                                     .OrderBy(p => p.Index)
                                     .ToListAsync(cancellationToken);
        return MapEntityToDomain(entity, postings);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<AccountRole, long>> GetBalancesAsync(
        CancellationToken cancellationToken = default)
    {
        var saved = await _context.AccountingBalances.AsNoTracking().ToListAsync(cancellationToken);
        var balances = saved.ToDictionary(b => (AccountRole)b.Account, b => b.BalanceMsat);

        // What this unit of work staged wins over what is saved
        foreach (var tracked in _context.AccountingBalances.Local)
            balances[(AccountRole)tracked.Account] = tracked.BalanceMsat;

        return balances;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<AccountRole, long>> SumPostingsAsync(
        DateTimeOffset? since, DateTimeOffset? until, CancellationToken cancellationToken = default)
    {
        var postings = _context.AccountingPostings.AsNoTracking();
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

        var entries = _context.AccountingEntries.AsNoTracking().Where(e => e.LedgerSeq > query.AfterLedgerSeq);
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

        if (query.Account is { } accountRole)
        {
            var account = (int)accountRole;
            entries = entries.Where(e => _context.AccountingPostings.Any(p => p.LedgerSeq == e.LedgerSeq
                                                                           && p.Account == account));
        }

        var entities = await entries.OrderBy(e => e.LedgerSeq).Take(query.Take).ToListAsync(cancellationToken);
        if (entities.Count == 0)
            return [];

        var ledgerSeqs = entities.Select(e => e.LedgerSeq).ToList();
        var postings = await _context.AccountingPostings.AsNoTracking()
                                     .Where(p => ledgerSeqs.Contains(p.LedgerSeq))
                                     .ToListAsync(cancellationToken);
        var postingsBySeq = postings.GroupBy(p => p.LedgerSeq)
                                    .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Index).ToList());
        return entities.Select(e => MapEntityToDomain(e, postingsBySeq.GetValueOrDefault(e.LedgerSeq) ?? []))
                       .ToList();
    }

    /// <inheritdoc />
    /// <remarks>Runs at once (see the class remarks), not at the unit of work's save.</remarks>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        // The bulk deletes bypass the change tracker: forget what it tracks of the books, or the next save would
        // write rows that no longer exist
        foreach (var tracked in _context.ChangeTracker.Entries()
                                        .Where(e => e.Entity is AccountingEntryEntity or AccountingPostingEntity
                                                                or AccountingBalanceEntity or AccountingCursorEntity)
                                        .ToList())
            tracked.State = EntityState.Detached;

        await _context.AccountingPostings.ExecuteDeleteAsync(cancellationToken);
        await _context.AccountingEntries.ExecuteDeleteAsync(cancellationToken);
        await _context.AccountingBalances.ExecuteDeleteAsync(cancellationToken);
        await _context.AccountingCursor.ExecuteDeleteAsync(cancellationToken);
    }

    private static AccountingEntry MapEntityToDomain(AccountingEntryEntity entity,
                                                     IEnumerable<AccountingPostingEntity> postings) =>
        new(entity.LedgerSeq, entity.EventKey, (AccountingEventKind)entity.Kind, entity.OccurredAt, entity.ChannelId,
            entity.PaymentHash,
            postings.Select(p => new AccountingPosting((AccountRole)p.Account, p.AmountMsat)).ToList(), entity.Note);
}