namespace NLightning.Domain.Accounting.Prices;

using Financial;

/// <summary>
/// Where the back-valuation job gets the BTC price of a time (NL-602 A3-T2, D-A11): the operator's CSV, an HTTP source
/// (mempool.space's historical price) or both. Only <c>PriceValuationService</c> and the operator's <c>prices fetch</c>
/// ask it, never a hot path.
/// </summary>
public interface IPriceSource
{
    /// <summary>
    /// The price of one BTC in <paramref name="currency"/> nearest at or before <paramref name="time"/> that the source
    /// knows, with the time it is for (the caller decides whether it is recent enough), or null when it has none or
    /// failed (a failure is logged, never thrown; only a cancellation is). The returned price is not stored: its
    /// <see cref="AccountingPrice.Id"/> is 0.
    /// </summary>
    Task<AccountingPrice?> GetPriceAsync(string currency, DateTimeOffset time,
                                         CancellationToken cancellationToken = default);
}