namespace NLightning.Domain.Accounting.Financial;

/// <summary>How a cost-basis lot was opened (<c>AccountingLots.Origin</c>, D-A9, D-A12). Never renumber.</summary>
public enum AccountingLotOrigin : byte
{
    /// <summary>An acquisition of the financial book: a deposit at its fiat cost or income at fair value.</summary>
    Acquisition = 1,

    /// <summary>An opening balance of the cutover, at the cutover's price (<c>BasisEstimated</c>).</summary>
    Opening = 2,

    /// <summary>A lot of <c>nltg accounting lots import</c>, which replaces the opening lots before the first close.</summary>
    Import = 3,

    /// <summary>The part of a lot split off by a projector that tracks lots per account (<c>ParentLotId</c>).</summary>
    Split = 4
}