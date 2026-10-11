namespace NLightning.Domain.Accounting.Prices;

using Financial;

/// <summary>
/// The stored prices and the back-valuation of the financial books, for <c>nltg accounting prices
/// import|list|fetch|replace</c> (IPC 45, NL-602 A3-T2, NL-693). Implemented by <c>PriceValuationService</c>.
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

    /// <summary>The immutable correction history of the selected prices, oldest first.</summary>
    Task<IReadOnlyList<AccountingPriceReplacementAudit>> ListReplacementAuditsAsync(IReadOnlyCollection<long> priceIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the configured sources for the price of every hour in [since, until) that holds no stored price yet (at
    /// most <see cref="AccountingPriceOptions.MaxFetchHoursPerCommand"/> hours), stores what they answer and values
    /// what it can value now.
    /// </summary>
    /// <exception cref="InvalidOperationException">No source is configured (<c>Source=None</c>).</exception>
    /// <exception cref="ArgumentException">A bad range.</exception>
    Task<AccountingPriceFetchResult> FetchAsync(DateTimeOffset since, DateTimeOffset? until,
                                                CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a stored price the operator found wrong (<c>prices replace</c>, NL-693) and re-values what it priced in
    /// the financial book: the open period's entries valued with it are projected again from the earliest of them (the
    /// financial projector's replay: lots, reliefs and gains as a rebuild would have them), and the lines of closed
    /// periods valued with it get a <c>Price</c> adjustment each entry in the open period with the change of their
    /// value (D-A8: a closed period is never rewritten). The row keeps its id and time; its source becomes
    /// <see cref="AccountingPriceSource.Manual"/> and its <c>FetchedAt</c> the time of the replacement; the old price,
    /// the operator's source and note are logged and written in the adjustments' notes.
    /// </summary>
    /// <exception cref="ArgumentException">A bad currency, price, source or note, or no stored price at that time (to
    /// the second).</exception>
    Task<AccountingPriceReplaceResult> ReplaceAsync(AccountingPriceReplacement replacement,
                                                    CancellationToken cancellationToken = default);

    /// <summary>Runs one back-valuation round now (serialized with the background rounds), asking the sources for
    /// at most <c>MaxFetchesPerRound</c> prices.</summary>
    Task<AccountingValuationRoundResult> ValueNowAsync(CancellationToken cancellationToken = default);
}

/// <summary>The operator's correction of a stored price (<c>prices replace</c>, NL-693).</summary>
/// <param name="Currency">The price's currency, or null for the configured one.</param>
/// <param name="Time">The stored price's time (matched to the second).</param>
/// <param name="Price">The right price of 1 BTC.</param>
/// <param name="Source">Where the right price comes from (free text, the audit trail), or null.</param>
/// <param name="Note">Why it is replaced (free text, the audit trail), or null.</param>
public sealed record AccountingPriceReplacement(
    string? Currency,
    DateTimeOffset Time,
    decimal Price,
    string? Source = null,
    string? Note = null)
{
    /// <summary>The longest <see cref="Source"/>.</summary>
    public const int MaxSourceLength = 100;

    /// <summary>The longest <see cref="Note"/>.</summary>
    public const int MaxNoteLength = 300;
}

/// <summary>What a price replacement changed (NL-693).</summary>
/// <param name="Price">The stored price after the replacement (its id and time kept).</param>
/// <param name="OldPrice">The price it replaced.</param>
/// <param name="OldSource">Where the replaced price came from.</param>
/// <param name="OldFetchedAt">When the replaced price was stored.</param>
/// <param name="Changed">False when the stored price already was the new one (nothing done).</param>
/// <param name="ReplayFromLedgerSeq">The ledger sequence the financial projector projects the open period again
/// from, or null when no open entry was valued with the price.</param>
/// <param name="OpenEntries">Open-period entries valued with the price (projected again).</param>
/// <param name="ClosedEntries">Entries of closed periods (and their adjustments) valued with the price.</param>
/// <param name="Adjustments">Price adjustments staged for them in the open period.</param>
/// <param name="LinesRepriced">Their lines whose value changed.</param>
public sealed record AccountingPriceReplaceResult(
    AccountingPrice Price,
    decimal OldPrice,
    AccountingPriceSource OldSource,
    DateTimeOffset OldFetchedAt,
    bool Changed,
    long? ReplayFromLedgerSeq,
    int OpenEntries,
    int ClosedEntries,
    int Adjustments,
    int LinesRepriced);

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