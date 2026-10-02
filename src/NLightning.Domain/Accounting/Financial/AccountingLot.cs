namespace NLightning.Domain.Accounting.Financial;

using Books;

/// <summary>
/// A cost-basis lot of the financial book (<c>AccountingLots</c>, D-A12): sats acquired at a time for a fiat cost, of
/// which <see cref="RemainingMsat"/> are not disposed of yet. Disposals relieve lots
/// (<see cref="AccountingLotRelief"/>) in the method's order; transfers between our own buckets leave them alone.
/// </summary>
/// <param name="Id">The storage id (0 until saved).</param>
/// <param name="AcquiredAt">When the sats were acquired (the order of FIFO and LIFO).</param>
/// <param name="Origin">How the lot was opened.</param>
/// <param name="SourceLedgerSeq">The financial entry that opened it (null for an imported lot).</param>
/// <param name="SourceAdjustment">That entry's adjustment number.</param>
/// <param name="Account">The asset account that holds the lot when lots are tracked per account, or null for the
/// node-wide pool.</param>
/// <param name="ParentLotId">The lot a split lot came from.</param>
/// <param name="OriginalMsat">The amount acquired, msat.</param>
/// <param name="RemainingMsat">The amount not disposed of yet, msat.</param>
/// <param name="FiatCost">The cost of <paramref name="OriginalMsat"/>, or null while unvalued.</param>
/// <param name="FiatCurrency">The ISO 4217 code of <paramref name="FiatCost"/>.</param>
/// <param name="PriceId">The <c>AccountingPrices</c> row of the cost, if any.</param>
/// <param name="BasisEstimated">True for the cutover's opening lots (D-A9).</param>
/// <param name="ClosedPeriodId">The period whose close recorded the lot, or null.</param>
public sealed record AccountingLot(
    long Id,
    DateTimeOffset AcquiredAt,
    AccountingLotOrigin Origin,
    long? SourceLedgerSeq,
    int SourceAdjustment,
    AccountRole? Account,
    long? ParentLotId,
    long OriginalMsat,
    long RemainingMsat,
    decimal? FiatCost,
    string? FiatCurrency,
    long? PriceId,
    bool BasisEstimated,
    string? ClosedPeriodId);