namespace NLightning.Domain.Accounting.Financial;

/// <summary>
/// The price of one BTC in a fiat currency at a time (<c>AccountingPrices</c>, D-A11): a posting is valued at the
/// nearest price at or before its time within <c>Accounting:Prices:MaxAge</c>. Stored once per currency and time; the
/// id is what a valued posting or lot keeps, so its value is reproducible.
/// </summary>
/// <param name="Id">The storage id (0 until saved).</param>
/// <param name="Currency">The ISO 4217 code (three upper-case letters).</param>
/// <param name="Time">The time the price is for (UTC).</param>
/// <param name="Price">The price of 1 BTC (8 decimal places are stored).</param>
/// <param name="Source">Where it came from.</param>
/// <param name="FetchedAt">When we stored it.</param>
public sealed record AccountingPrice(
    long Id,
    string Currency,
    DateTimeOffset Time,
    decimal Price,
    AccountingPriceSource Source,
    DateTimeOffset FetchedAt);