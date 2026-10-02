using System.Globalization;

namespace NLightning.Client.Printers;

using Transport.Ipc.Responses;

/// <summary>
/// Prints <c>accounting prices import|list|fetch</c> (NL-602 A3-T2). Prices arrive as invariant text and times are
/// printed in UTC, so the output does not depend on the client's culture (the counts are non-negative integers).
/// </summary>
public sealed class AccountingPricesPrinter : IPrinter<AccountingPricesIpcResponse>
{
    private readonly TextWriter _output;

    public AccountingPricesPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    /// <inheritdoc />
    public void Print(AccountingPricesIpcResponse item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Prices is { } prices)
        {
            _output.WriteLine($"{prices.Count} {item.Currency} price(s) per BTC");
            foreach (var price in prices)
            {
                _output.WriteLine($"  {Time(price.TimeUnixSeconds)}  {price.Price,20}  {price.SourceName,-6}  "
                                + $"(time {price.TimeUnixSeconds}, id {price.Id})");
            }

            if (prices.Count > 0)
                _output.WriteLine($"Next page: --since {prices[^1].TimeUnixSeconds + 1}");
        }

        if (item.Import is { } import)
        {
            _output.WriteLine($"Imported {import.Added} {item.Currency} price(s); {import.AlreadyStored} "
                            + "time(s) already had a price and kept it");
            PrintValuation(import.Valuation);
        }

        if (item.Fetch is { } fetch)
        {
            _output.WriteLine($"Fetched {item.Currency} prices for {Time(fetch.SinceUnixSeconds)} to "
                            + $"{Time(fetch.UntilUnixSeconds)}: {fetch.Hours} hour(s), "
                            + $"{fetch.AlreadyCovered} already stored, {fetch.Requested} asked, "
                            + $"{fetch.Stored} stored, {fetch.Unavailable} unavailable");
            PrintValuation(fetch.Valuation);
        }
    }

    private void PrintValuation(AccountingValuationRoundIpc? valuation)
    {
        if (valuation is null)
        {
            _output.WriteLine("No valuation: the books are off");
            return;
        }

        _output.WriteLine($"Valued {valuation.Valued} of {valuation.Listed} unvalued posting(s); "
                        + $"{valuation.Unpriced} still without a price"
                        + (valuation.Deferred > 0
                             ? $", {valuation.Deferred} waiting for their hour's price"
                             : string.Empty)
                        + (valuation.LateValuations > 0
                             ? $", {valuation.LateValuations} of closed periods adjusted"
                             : string.Empty)
                        + (valuation.ClosedLeftUnvalued > 0
                             ? $", {valuation.ClosedLeftUnvalued} of closed periods left unvalued"
                             : string.Empty));
    }

    private static string Time(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToString("yyyy-MM-dd HH:mm:ss'Z'",
                                                                  CultureInfo.InvariantCulture);
}