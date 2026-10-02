namespace NLightning.Domain.Accounting.Prices;

/// <summary>Which price sources the back-valuation job asks (<c>Accounting:Prices:Source</c>, D-A11).</summary>
public enum AccountingPriceSourceMode
{
    /// <summary>No source: no file read, no request; only prices stored by <c>prices import</c> value postings.</summary>
    None = 0,

    /// <summary>The operator's price file only.</summary>
    Csv = 1,

    /// <summary>The HTTP source only.</summary>
    Http = 2,

    /// <summary>The price file first, then the HTTP source for what the file does not hold (default).</summary>
    Both = 3
}