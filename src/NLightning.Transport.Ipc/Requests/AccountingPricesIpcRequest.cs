using System.Globalization;
using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Accounting.Prices;
using Domain.Client.Constants;
using Domain.Client.Exceptions;
using Domain.Client.Requests;

/// <summary>
/// The arguments of <c>accounting prices import|list|fetch|replace</c> (ClientCommand 45 key 10, NL-602 A3-T2,
/// NL-693). Prices travel
/// as invariant decimal text. Keys are append-only.
/// </summary>
[MessagePackObject]
public sealed class AccountingPricesIpcRequest
{
    /// <summary>The most rows one request carries (the client sends a larger file in several requests).</summary>
    public const int MaxRowsPerRequest = 10_000;

    /// <summary>The currency, or null for the node's <c>Accounting:Prices:Currency</c>.</summary>
    [Key(0)] public string? Currency { get; set; }

    /// <summary>import: the prices.</summary>
    [Key(1)] public List<AccountingPriceRowIpc>? Rows { get; set; }

    /// <summary>list, fetch: the start of the range (Unix seconds).</summary>
    [Key(2)] public long? SinceUnixSeconds { get; set; }

    /// <summary>list, fetch: the end of the range, exclusive (Unix seconds).</summary>
    [Key(3)] public long? UntilUnixSeconds { get; set; }

    /// <summary>list: the most prices (100, at most 1,000).</summary>
    [Key(4)] public int Limit { get; set; } = 100;

    /// <summary>replace: the stored price's time (Unix seconds) and the new price (key 6), NL-693.</summary>
    [Key(5)] public long? ReplaceTimeUnixSeconds { get; set; }

    /// <summary>replace: the new price of 1 BTC, invariant decimal text.</summary>
    [Key(6)] public string? ReplacePrice { get; set; }

    /// <summary>replace: where the new price comes from (the audit trail).</summary>
    [Key(7)] public string? Source { get; set; }

    /// <summary>replace: why the price is replaced (the audit trail).</summary>
    [Key(8)] public string? Note { get; set; }

    /// <exception cref="ClientException">Too many rows, a bad price or a time out of range.</exception>
    public AccountingPricesClientRequest ToClientRequest()
    {
        if (Rows is { Count: > MaxRowsPerRequest })
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      $"At most {MaxRowsPerRequest} prices per request ({Rows.Count} sent).");

        var points = new List<AccountingPricePoint>(Rows?.Count ?? 0);
        for (var i = 0; i < (Rows?.Count ?? 0); i++)
        {
            var row = Rows![i];
            if (row is null
                || !decimal.TryParse(row.Price, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                                     out var price))
                throw new ClientException(ErrorCodes.InvalidOperation, $"Price row {i + 1} is not a price.");

            points.Add(new AccountingPricePoint(ToTime(row.TimeUnixSeconds, $"price row {i + 1}"), price));
        }

        AccountingPricePoint? replacement = null;
        if (ReplaceTimeUnixSeconds is { } replaceTime)
        {
            if (string.IsNullOrWhiteSpace(ReplacePrice)
             || !decimal.TryParse(ReplacePrice.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                                  out var replacePrice))
                throw new ClientException(ErrorCodes.InvalidOperation, "The new price is not a price.");

            replacement = new AccountingPricePoint(ToTime(replaceTime, "the replaced price"), replacePrice);
        }

        return new AccountingPricesClientRequest
        {
            Currency = Currency,
            Points = points,
            Since = SinceUnixSeconds is { } since ? ToTime(since, "since") : null,
            Until = UntilUnixSeconds is { } until ? ToTime(until, "until") : null,
            Limit = Limit,
            Replacement = replacement,
            Source = string.IsNullOrWhiteSpace(Source) ? null : Source.Trim(),
            Note = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim()
        };
    }

    private static DateTimeOffset ToTime(long seconds, string what)
    {
        if (seconds < 0 || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            throw new ClientException(ErrorCodes.InvalidOperation, $"The time of {what} is out of range.");

        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }
}

/// <summary>One price of an import: its time (Unix seconds) and the price of 1 BTC as invariant decimal text.</summary>
[MessagePackObject]
public sealed class AccountingPriceRowIpc
{
    [Key(0)] public long TimeUnixSeconds { get; set; }

    [Key(1)] public string Price { get; set; } = string.Empty;
}