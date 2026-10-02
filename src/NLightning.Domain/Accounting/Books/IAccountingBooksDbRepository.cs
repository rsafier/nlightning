namespace NLightning.Domain.Accounting.Books;

/// <summary>
/// The operational books' tables (plan §6.1, §6.3): entries with their postings, a running balance per account and the
/// projector's cursor. Writes are staged and committed by the unit of work's save: the projector saves its cursor with
/// the entries it produced, so the projection is exactly-once.
/// </summary>
public interface IAccountingBooksDbRepository
{
    /// <summary>The ledger sequence of the last projected event (0 = none).</summary>
    Task<long> GetCursorAsync(CancellationToken cancellationToken = default);

    /// <summary>Stages the cursor.</summary>
    Task SetCursorAsync(long ledgerSeq, CancellationToken cancellationToken = default);

    /// <summary>Stages an entry and adds its postings to the running balances. The entry must balance.</summary>
    Task AddEntryAsync(AccountingEntry entry, CancellationToken cancellationToken = default);

    /// <summary>The entry of an event key (saved or staged), or null.</summary>
    Task<AccountingEntry?> GetEntryByKeyAsync(string eventKey, CancellationToken cancellationToken = default);

    /// <summary>The running balance of every account with postings.</summary>
    Task<IReadOnlyDictionary<AccountRole, long>> GetBalancesAsync(CancellationToken cancellationToken = default);

    /// <summary>The sum of the postings per account of the entries that occurred in [since, until).</summary>
    Task<IReadOnlyDictionary<AccountRole, long>> SumPostingsAsync(DateTimeOffset? since, DateTimeOffset? until,
                                                                  CancellationToken cancellationToken = default);

    /// <summary>Entries with their postings, in ledger order.</summary>
    Task<IReadOnlyList<AccountingEntry>> ListEntriesAsync(AccountingEntryQuery query,
                                                          CancellationToken cancellationToken = default);

    /// <summary>Stages the deletion of every entry, posting, balance and the cursor (a rebuild).</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}