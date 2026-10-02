namespace NLightning.Domain.Accounting.Financial;

/// <summary>
/// The stored prices (<c>AccountingPrices</c>, A3-T2). One row per currency and time; a stored price is never changed,
/// because valued postings and lots keep its id. Writes are staged and committed by the unit of work's save; the id of
/// a new row exists only after that save, so a job that values postings saves its new prices first.
/// </summary>
public interface IAccountingPriceDbRepository
{
    /// <summary>Stages a price unless one exists (saved or staged) for its currency and time; returns whether it was
    /// staged.</summary>
    Task<bool> TryAddAsync(AccountingPrice price, CancellationToken cancellationToken = default);

    /// <summary>The price with this id, or null.</summary>
    Task<AccountingPrice?> GetByIdAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>The latest saved price of <paramref name="currency"/> at or before <paramref name="time"/> and not
    /// older than <paramref name="maxAge"/> before it (D-A11), or null.</summary>
    Task<AccountingPrice?> GetAtOrBeforeAsync(string currency, DateTimeOffset time, TimeSpan maxAge,
                                              CancellationToken cancellationToken = default);

    /// <summary>Saved prices of <paramref name="currency"/> in [since, until), oldest first, at most
    /// <paramref name="take"/>.</summary>
    Task<IReadOnlyList<AccountingPrice>> ListAsync(string currency, DateTimeOffset? since, DateTimeOffset? until,
                                                   int take, CancellationToken cancellationToken = default);
}