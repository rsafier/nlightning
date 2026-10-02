namespace NLightning.Domain.Accounting.Books;

/// <summary>
/// The books' tables (plan §6.1, §6.3): entries with their postings, a running balance per account and the projectors'
/// cursors, one per <see cref="AccountingBook"/> (D-A7). Writes are staged and committed by the unit of work's save: a
/// projector saves its cursor with the entries it produced, so the projection is exactly-once.
/// </summary>
/// <remarks>
/// The members without an <see cref="AccountingBook"/> argument read and write the operational book (A2). The
/// book-aware members (migration <c>AddAccountingFinancial</c>, A3-T0) default to those for the operational book and
/// throw <see cref="NotSupportedException"/> for another one, so test doubles that keep only the operational book need
/// not implement them.
/// </remarks>
public interface IAccountingBooksDbRepository
{
    /// <summary>The ledger sequence of the last projected event of the operational book (0 = none).</summary>
    Task<long> GetCursorAsync(CancellationToken cancellationToken = default);

    /// <summary>Stages the operational book's cursor.</summary>
    Task SetCursorAsync(long ledgerSeq, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages an entry (in its <see cref="AccountingEntry.Book"/>) and adds its postings to the running balances. The
    /// entry must balance.
    /// </summary>
    Task AddEntryAsync(AccountingEntry entry, CancellationToken cancellationToken = default);

    /// <summary>The operational entry of an event key (saved or staged), or null.</summary>
    Task<AccountingEntry?> GetEntryByKeyAsync(string eventKey, CancellationToken cancellationToken = default);

    /// <summary>The running balance of every account of the operational book with postings.</summary>
    Task<IReadOnlyDictionary<AccountRole, long>> GetBalancesAsync(CancellationToken cancellationToken = default);

    /// <summary>The sum of the postings per account of the operational entries that occurred in [since, until).</summary>
    Task<IReadOnlyDictionary<AccountRole, long>> SumPostingsAsync(DateTimeOffset? since, DateTimeOffset? until,
                                                                  CancellationToken cancellationToken = default);

    /// <summary>Entries with their postings of <see cref="AccountingEntryQuery.Book"/>, in ledger order.</summary>
    Task<IReadOnlyList<AccountingEntry>> ListEntriesAsync(AccountingEntryQuery query,
                                                          CancellationToken cancellationToken = default);

    /// <summary>Deletes every entry, posting and balance of the operational book and its cursor at once (a rebuild;
    /// not staged: it runs before the rebuild's first save, so a crash after it rebuilds from 0). The financial book is
    /// left alone.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>The ledger sequence of the last operational entry the book projected (0 = none).</summary>
    Task<long> GetCursorAsync(AccountingBook book, CancellationToken cancellationToken = default) =>
        book == AccountingBook.Operational ? GetCursorAsync(cancellationToken) : throw NotSupported(book);

    /// <summary>Stages the book's cursor.</summary>
    Task SetCursorAsync(AccountingBook book, long ledgerSeq, CancellationToken cancellationToken = default) =>
        book == AccountingBook.Operational ? SetCursorAsync(ledgerSeq, cancellationToken) : throw NotSupported(book);

    /// <summary>Every entry of an event key in the book (saved or staged), the projected one and its adjustments, in
    /// adjustment order.</summary>
    async Task<IReadOnlyList<AccountingEntry>> GetEntriesByKeyAsync(AccountingBook book, string eventKey,
                                                                    CancellationToken cancellationToken = default)
    {
        if (book != AccountingBook.Operational)
            throw NotSupported(book);

        return await GetEntryByKeyAsync(eventKey, cancellationToken) is { } entry ? [entry] : [];
    }

    /// <summary>The running balance of every account of the book with postings.</summary>
    async Task<IReadOnlyList<AccountingAccountBalance>> GetAccountBalancesAsync(
        AccountingBook book, CancellationToken cancellationToken = default)
    {
        if (book != AccountingBook.Operational)
            throw NotSupported(book);

        var balances = await GetBalancesAsync(cancellationToken);
        return balances.Select(b => new AccountingAccountBalance(book, b.Key, null, b.Value, 0m)).ToList();
    }

    /// <summary>
    /// Stages the fiat value of one posting (A3-T2's back-valuation) and adds it to its account's running fiat balance;
    /// the entry loses <see cref="AccountingEntryFlags.Unvalued"/> once none of its postings is left unvalued. Returns
    /// false when the posting does not exist or is already valued.
    /// </summary>
    Task<bool> SetPostingValueAsync(AccountingPostingKey posting, decimal fiatAmount, string fiatCurrency, long priceId,
                                    CancellationToken cancellationToken = default) =>
        throw NotSupported(posting.Book);

    /// <summary>Up to <paramref name="take"/> postings of the book without a fiat value, oldest first (the
    /// back-valuation's work list).</summary>
    Task<IReadOnlyList<AccountingUnvaluedPosting>> ListUnvaluedPostingsAsync(
        AccountingBook book, int take, CancellationToken cancellationToken = default) =>
        throw NotSupported(book);

    /// <summary>
    /// Up to <paramref name="take"/> postings of the book without a fiat value that come after
    /// <paramref name="after"/> in the work list's order (time, ledger sequence, adjustment, line), oldest first; null
    /// starts at the beginning. A job pages past the postings it cannot value yet with it (A3-T2).
    /// </summary>
    Task<IReadOnlyList<AccountingUnvaluedPosting>> ListUnvaluedPostingsAsync(
        AccountingBook book, AccountingUnvaluedPostingCursor? after, int take,
        CancellationToken cancellationToken = default) =>
        after is null ? ListUnvaluedPostingsAsync(book, take, cancellationToken) : throw NotSupported(book);

    /// <summary>Stages the closed period of every entry of the book that occurred in [start, end) and has none yet
    /// (A3-T5); returns how many.</summary>
    Task<int> MarkEntriesClosedAsync(AccountingBook book, string periodId, DateTimeOffset start, DateTimeOffset end,
                                     CancellationToken cancellationToken = default) =>
        throw NotSupported(book);

    /// <summary>Deletes the book's entries, postings, balances and cursor at once, like <see cref="ClearAsync"/>.</summary>
    Task ClearAsync(AccountingBook book, CancellationToken cancellationToken = default) =>
        book == AccountingBook.Operational ? ClearAsync(cancellationToken) : throw NotSupported(book);

    private static NotSupportedException NotSupported(AccountingBook book) =>
        new($"This repository does not store the {book} book.");
}