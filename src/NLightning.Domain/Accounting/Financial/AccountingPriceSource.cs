namespace NLightning.Domain.Accounting.Financial;

/// <summary>Where a stored price came from (<c>AccountingPrices.Source</c>, D-A11). Never renumber.</summary>
public enum AccountingPriceSource : byte
{
    /// <summary>The operator's price file (<c>&lt;configPath&gt;/prices.csv</c>).</summary>
    Csv = 1,

    /// <summary>The HTTP price source, queried by the back-valuation job.</summary>
    Http = 2,

    /// <summary>Rows the operator sent with <c>nltg accounting prices import</c>.</summary>
    Import = 3
}