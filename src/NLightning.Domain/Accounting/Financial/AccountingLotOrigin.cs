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

    /// <summary>Reserved (never written): a part moved to another bucket keeps the origin of the lot it came from and
    /// names it in <c>ParentLotId</c> (NL-657).</summary>
    Split = 4,

    /// <summary>
    /// A bucket's debt to another (NL-657): msat the bucket gave out before lots came into it (the clearing account
    /// spent before the wallet's own event, a rebalance received before it was paid). <c>Bucket</c> is the debtor,
    /// <c>Lender</c> the bucket that gave the lots (or holds the claim), <c>RemainingMsat</c> what is still owed and
    /// <c>FiatCost</c> the cost of what was lent; msat that come back from the lender settle it
    /// (<see cref="AccountingLotReliefKind.Settlement"/>). Never an asset: the reports leave it out of the open lots.
    /// </summary>
    Debt = 5
}