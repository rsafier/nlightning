namespace NLightning.Domain.Accounting.Prices;

using Constants;
using Financial;

/// <summary>
/// The valuation rules of the financial books (D-A11, NL-602 A3-T2): the nearest price at or before a time within a
/// maximum age, and a posting's fiat value at 8 places.
/// </summary>
public static class AccountingValuation
{
    /// <summary>Millisatoshis per BTC.</summary>
    public const decimal MsatPerBtc = 100_000_000_000m;

    /// <summary>
    /// The fiat value of <paramref name="amountMsat"/> at <paramref name="price"/> per BTC, signed like the amount and
    /// rounded to 8 places (banker's rounding). The amount is converted to BTC first (exact in <c>decimal</c>), so no
    /// stored price and amount overflow.
    /// </summary>
    public static decimal FiatValue(long amountMsat, decimal price)
    {
        var btc = amountMsat / MsatPerBtc;
        return Math.Round(btc * price, AccountingSchemaLimits.FiatScale, MidpointRounding.ToEven);
    }

    /// <summary>The start of the UTC hour that holds <paramref name="time"/>.</summary>
    public static DateTimeOffset HourStart(DateTimeOffset time)
    {
        var ticks = time.UtcTicks;
        return new DateTimeOffset(ticks - ticks % TimeSpan.TicksPerHour, TimeSpan.Zero);
    }

    /// <summary>Whether a price for <paramref name="priceTime"/> may value something at <paramref name="at"/>: at or
    /// before it and no older than <paramref name="maxAge"/> (the boundary included).</summary>
    public static bool IsUsable(DateTimeOffset priceTime, DateTimeOffset at, TimeSpan maxAge) =>
        priceTime <= at && at - priceTime <= maxAge;

    /// <summary>
    /// The latest price of <paramref name="sortedPrices"/> (oldest first) at or before <paramref name="at"/> and no
    /// older than <paramref name="maxAge"/>, or null (D-A11's rule, the same as
    /// <see cref="IAccountingPriceDbRepository.GetAtOrBeforeAsync"/>).
    /// </summary>
    public static T? NearestAtOrBefore<T>(IReadOnlyList<T> sortedPrices, Func<T, DateTimeOffset> timeOf,
                                          DateTimeOffset at, TimeSpan maxAge) where T : class
    {
        ArgumentNullException.ThrowIfNull(sortedPrices);
        ArgumentNullException.ThrowIfNull(timeOf);

        // The last index whose time is at or before 'at'
        int low = 0, high = sortedPrices.Count - 1, found = -1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (timeOf(sortedPrices[middle]) <= at)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (found < 0)
            return null;

        var candidate = sortedPrices[found];
        return IsUsable(timeOf(candidate), at, maxAge) ? candidate : null;
    }
}