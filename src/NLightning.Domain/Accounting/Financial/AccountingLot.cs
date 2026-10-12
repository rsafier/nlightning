namespace NLightning.Domain.Accounting.Financial;

/// <summary>
/// A cost-basis lot of the financial book (<c>AccountingLots</c>, D-A12): sats acquired at a time for a fiat cost, of
/// which <see cref="RemainingMsat"/> are not disposed of yet, held by one bucket (NL-657). Disposals relieve lots
/// (<see cref="AccountingLotRelief"/>) in the method's order; a transfer between our own buckets relieves the part it
/// moves (<see cref="AccountingLotReliefKind.Move"/>) and opens a lot of the destination for it that keeps the cost,
/// origin and <see cref="HeldSince"/> of the moved part and names it in <see cref="ParentLotId"/>.
/// </summary>
/// <param name="Id">The storage id (0 until saved).</param>
/// <param name="AcquiredAt">When the lot came to be in the book: the acquisition's time, or the moving entry's for a
/// moved part (a close records the lots that existed at its end by this time).</param>
/// <param name="Origin">How the lot was opened (a moved part keeps the origin of the lot it came from).</param>
/// <param name="SourceLedgerSeq">The financial entry that opened it (null for an imported lot).</param>
/// <param name="SourceAdjustment">That entry's adjustment number.</param>
/// <param name="Bucket">The bucket that holds the lot, or null for a lot of the node-wide pool of a book projected
/// before lots were kept per bucket, or an imported lot (D-A9) its opening balance has not taken yet.</param>
/// <param name="ParentLotId">The lot a moved part came from.</param>
/// <param name="OriginalMsat">The amount acquired, msat.</param>
/// <param name="RemainingMsat">The amount not disposed of yet, msat.</param>
/// <param name="FiatCost">The cost of <paramref name="OriginalMsat"/>, or null while unvalued.</param>
/// <param name="FiatCurrency">The ISO 4217 code of <paramref name="FiatCost"/>.</param>
/// <param name="PriceId">The <c>AccountingPrices</c> row of the cost, if any.</param>
/// <param name="BasisEstimated">True for the cutover's opening lots (D-A9) and the parts moved from them.</param>
/// <param name="ClosedPeriodId">The period whose close recorded the lot, or null.</param>
public sealed record AccountingLot(
    long Id,
    DateTimeOffset AcquiredAt,
    AccountingLotOrigin Origin,
    long? SourceLedgerSeq,
    int SourceAdjustment,
    AccountingLotBucket? Bucket,
    long? ParentLotId,
    long OriginalMsat,
    long RemainingMsat,
    decimal? FiatCost,
    string? FiatCurrency,
    long? PriceId,
    bool BasisEstimated,
    string? ClosedPeriodId)
{
    /// <summary>
    /// When the sats were acquired, for a part moved from another lot (the original acquisition's time); null = the lot's
    /// own <see cref="AcquiredAt"/>. FIFO and LIFO order by it and the holding period of a gain starts at it.
    /// </summary>
    public DateTimeOffset? HeldSince { get; init; }

    /// <summary>The bucket a debt (<see cref="AccountingLotOrigin.Debt"/>) is owed to; null for any other lot.</summary>
    public AccountingLotBucket? Lender { get; init; }

    /// <summary>The acquisition time the cost-basis method and the holding period use.</summary>
    public DateTimeOffset HeldSinceOrAcquired => HeldSince ?? AcquiredAt;

    /// <summary>Whether this is a bucket's debt rather than sats it holds.</summary>
    public bool IsDebt => Origin == AccountingLotOrigin.Debt;
}