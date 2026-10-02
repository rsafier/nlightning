namespace NLightning.Domain.Accounting.Prices;

using Financial;

/// <summary>
/// The stored prices and the back-valuation of the financial books, for <c>nltg accounting prices
/// import|list|fetch</c> (IPC 45, NL-602 A3-T2). Implemented by <c>PriceValuationService</c>.
/// </summary>
public interface IAccountingPrices
{
    /// <summary>The configured base currency (<c>Accounting:Prices:Currency</c>).</summary>
    string Currency { get; }

    /// <summary>
    /// Stores the operator's prices (source <see cref="AccountingPriceSource.Import"/>; a time already stored keeps its
    /// price, as every stored price does) and values what they can value now.
    /// </summary>
    /// <param name="currency">The prices' currency, or null for <see cref="Currency"/>.</param>
    /// <param name="points">The prices.</param>
    /// <param name="cancellationToken">Cancels the import between saves.</param>
    /// <exception cref="ArgumentException">A bad currency or price (nothing is stored).</exception>
    Task<AccountingPriceImportResult> ImportAsync(string? currency, IReadOnlyList<AccountingPricePoint> points,
                                                  CancellationToken cancellationToken = default);

    /// <summary>Stored prices of <paramref name="currency"/> (or <see cref="Currency"/>) in [since, until), oldest
    /// first, at most <paramref name="take"/>.</summary>
    Task<IReadOnlyList<AccountingPrice>> ListAsync(string? currency, DateTimeOffset? since, DateTimeOffset? until,
                                                   int take, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the configured sources for the price of every hour in [since, until) that holds no stored price yet (at
    /// most <see cref="AccountingPriceOptions.MaxFetchHoursPerCommand"/> hours), stores what they answer and values
    /// what it can value now.
    /// </summary>
    /// <exception cref="InvalidOperationException">No source is configured (<c>Source=None</c>).</exception>
    /// <exception cref="ArgumentException">A bad range.</exception>
    Task<AccountingPriceFetchResult> FetchAsync(DateTimeOffset since, DateTimeOffset? until,
                                                CancellationToken cancellationToken = default);

    /// <summary>Runs one back-valuation round now (serialized with the background rounds), asking the sources for
    /// at most <c>MaxFetchesPerRound</c> prices.</summary>
    Task<AccountingValuationRoundResult> ValueNowAsync(CancellationToken cancellationToken = default);
}

/// <summary>What an import stored.</summary>
/// <param name="Currency">The prices' currency.</param>
/// <param name="Added">New prices stored.</param>
/// <param name="AlreadyStored">Times that already had a price (kept).</param>
/// <param name="Valuation">The valuation round that followed, or null when the books are off.</param>
public sealed record AccountingPriceImportResult(
    string Currency,
    int Added,
    int AlreadyStored,
    AccountingValuationRoundResult? Valuation);

/// <summary>What a fetch asked and stored.</summary>
/// <param name="Currency">The currency.</param>
/// <param name="Since">The first hour asked for.</param>
/// <param name="Until">The end of the range (exclusive).</param>
/// <param name="Hours">The hours in the range.</param>
/// <param name="AlreadyCovered">Hours that already held a stored price (not asked).</param>
/// <param name="Requested">Hours the sources were asked for.</param>
/// <param name="Stored">New prices stored.</param>
/// <param name="Unavailable">Asks that returned no price.</param>
/// <param name="Valuation">The valuation round that followed, or null when the books are off.</param>
public sealed record AccountingPriceFetchResult(
    string Currency,
    DateTimeOffset Since,
    DateTimeOffset Until,
    int Hours,
    int AlreadyCovered,
    int Requested,
    int Stored,
    int Unavailable,
    AccountingValuationRoundResult? Valuation);

/// <summary>One back-valuation round.</summary>
/// <param name="Listed">Unvalued postings of the financial book the round looked at.</param>
/// <param name="Valued">Postings it gave a fiat value.</param>
/// <param name="Fetched">Prices it asked the sources for.</param>
/// <param name="Stored">New prices it stored.</param>
/// <param name="LateValuations">Postings of a closed period handed to the adjustment rule (never filled, D-A8).</param>
/// <param name="ClosedLeftUnvalued">Postings of a closed period with a price that no adjustment rule took.</param>
/// <param name="Unpriced">Postings left without a price within <c>MaxAge</c>.</param>
public sealed record AccountingValuationRoundResult(
    int Listed,
    int Valued,
    int Fetched,
    int Stored,
    int LateValuations,
    int ClosedLeftUnvalued,
    int Unpriced)
{
    public static AccountingValuationRoundResult Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);

    /// <summary>Postings with a price within <c>MaxAge</c> that wait for the price of their own hour, which a later
    /// round asks the source for.</summary>
    public int Deferred { get; init; }
}