using System.Globalization;
using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Accounting.Prices;
using Domain.Client.Responses;

/// <summary>
/// The answer of <c>accounting prices import|list|fetch</c> (ClientCommand 45 key 10, NL-602 A3-T2): the field of the
/// action is set; prices and amounts are invariant decimal text. Keys are append-only.
/// </summary>
[MessagePackObject]
public sealed class AccountingPricesIpcResponse
{
    [Key(0)] public required string Currency { get; init; }

    /// <summary>list: the prices, oldest first.</summary>
    [Key(1)] public List<AccountingPriceIpc>? Prices { get; init; }

    /// <summary>import: what was stored.</summary>
    [Key(2)] public AccountingPriceImportIpc? Import { get; init; }

    /// <summary>fetch: what was asked and stored.</summary>
    [Key(3)] public AccountingPriceFetchIpc? Fetch { get; init; }

    public static AccountingPricesIpcResponse FromClientResponse(AccountingPricesClientResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new AccountingPricesIpcResponse
        {
            Currency = response.Currency,
            Prices = response.Prices?.Select(p => new AccountingPriceIpc
            {
                Id = p.Id,
                TimeUnixSeconds = p.Time.ToUnixTimeSeconds(),
                Price = p.Price.ToString(CultureInfo.InvariantCulture),
                Source = (int)p.Source,
                SourceName = p.Source.ToString(),
                FetchedAtUnixSeconds = p.FetchedAt.ToUnixTimeSeconds()
            }).ToList(),
            Import = response.Import is { } import
                         ? new AccountingPriceImportIpc
                         {
                             Added = import.Added,
                             AlreadyStored = import.AlreadyStored,
                             Valuation = AccountingValuationRoundIpc.From(import.Valuation)
                         }
                         : null,
            Fetch = response.Fetch is { } fetch
                        ? new AccountingPriceFetchIpc
                        {
                            SinceUnixSeconds = fetch.Since.ToUnixTimeSeconds(),
                            UntilUnixSeconds = fetch.Until.ToUnixTimeSeconds(),
                            Hours = fetch.Hours,
                            AlreadyCovered = fetch.AlreadyCovered,
                            Requested = fetch.Requested,
                            Stored = fetch.Stored,
                            Unavailable = fetch.Unavailable,
                            Valuation = AccountingValuationRoundIpc.From(fetch.Valuation)
                        }
                        : null
        };
    }
}

/// <summary>A stored price (NL-602 A3-T2).</summary>
[MessagePackObject]
public sealed class AccountingPriceIpc
{
    [Key(0)] public required long Id { get; init; }
    [Key(1)] public required long TimeUnixSeconds { get; init; }

    /// <summary>The price of 1 BTC, invariant decimal text.</summary>
    [Key(2)] public required string Price { get; init; }

    /// <summary>The <c>AccountingPriceSource</c> value (1 csv, 2 http, 3 import).</summary>
    [Key(3)] public required int Source { get; init; }

    [Key(4)] public required string SourceName { get; init; }
    [Key(5)] public required long FetchedAtUnixSeconds { get; init; }
}

/// <summary>What an import stored (NL-602 A3-T2).</summary>
[MessagePackObject]
public sealed class AccountingPriceImportIpc
{
    [Key(0)] public required int Added { get; init; }
    [Key(1)] public required int AlreadyStored { get; init; }

    /// <summary>The valuation round that followed; null when the books are off.</summary>
    [Key(2)] public AccountingValuationRoundIpc? Valuation { get; init; }
}

/// <summary>What a fetch asked and stored (NL-602 A3-T2).</summary>
[MessagePackObject]
public sealed class AccountingPriceFetchIpc
{
    [Key(0)] public required long SinceUnixSeconds { get; init; }
    [Key(1)] public required long UntilUnixSeconds { get; init; }
    [Key(2)] public required int Hours { get; init; }
    [Key(3)] public required int AlreadyCovered { get; init; }
    [Key(4)] public required int Requested { get; init; }
    [Key(5)] public required int Stored { get; init; }
    [Key(6)] public required int Unavailable { get; init; }

    /// <summary>The valuation round that followed; null when the books are off.</summary>
    [Key(7)] public AccountingValuationRoundIpc? Valuation { get; init; }
}

/// <summary>One back-valuation round (NL-602 A3-T2).</summary>
[MessagePackObject]
public sealed class AccountingValuationRoundIpc
{
    [Key(0)] public required int Listed { get; init; }
    [Key(1)] public required int Valued { get; init; }
    [Key(2)] public required int Fetched { get; init; }
    [Key(3)] public required int Stored { get; init; }
    [Key(4)] public required int LateValuations { get; init; }
    [Key(5)] public required int ClosedLeftUnvalued { get; init; }
    [Key(6)] public required int Unpriced { get; init; }

    /// <summary>Postings waiting for the price of their own hour (a later round asks for it).</summary>
    [Key(7)] public int Deferred { get; init; }

    public static AccountingValuationRoundIpc? From(AccountingValuationRoundResult? round) =>
        round is null
            ? null
            : new AccountingValuationRoundIpc
            {
                Listed = round.Listed,
                Valued = round.Valued,
                Fetched = round.Fetched,
                Stored = round.Stored,
                LateValuations = round.LateValuations,
                ClosedLeftUnvalued = round.ClosedLeftUnvalued,
                Unpriced = round.Unpriced,
                Deferred = round.Deferred
            };
}