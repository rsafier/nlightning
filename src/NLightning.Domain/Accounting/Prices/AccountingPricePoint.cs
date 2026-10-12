namespace NLightning.Domain.Accounting.Prices;

/// <summary>One price of a price file or an import: the price of 1 BTC at a time (UTC), in the file's currency.</summary>
public sealed record AccountingPricePoint(DateTimeOffset Time, decimal Price);