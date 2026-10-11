namespace NLightning.Domain.Accounting.Financial;

/// <summary>
/// The part of a lot an entry used (<c>AccountingLotReliefs</c>, D-A12): for a disposal the realized gain is
/// <see cref="Proceeds"/> − <see cref="FiatCostRelieved"/>, pending valuation while either is null; a move to another
/// bucket or the settlement of a debt realizes nothing (<see cref="Kind"/>, NL-657).
/// </summary>
/// <param name="Id">The storage id (0 until saved).</param>
/// <param name="LotId">The lot.</param>
/// <param name="LedgerSeq">The relieving financial entry's ledger sequence.</param>
/// <param name="Adjustment">That entry's adjustment number.</param>
/// <param name="RelievedAt">When the disposal happened (the entry's time).</param>
/// <param name="Msat">The amount taken from the lot, msat.</param>
/// <param name="FiatCostRelieved">The lot's cost of <paramref name="Msat"/>, or null while the lot is unvalued.</param>
/// <param name="Proceeds">What the disposal fetched in fiat (0 for a loss with no proceeds; the cost for a move or a
/// settlement), or null while unvalued.</param>
/// <param name="ClosedPeriodId">The period whose close holds the relief, or null.</param>
public sealed record AccountingLotRelief(
    long Id,
    long LotId,
    long LedgerSeq,
    int Adjustment,
    DateTimeOffset RelievedAt,
    long Msat,
    decimal? FiatCostRelieved,
    decimal? Proceeds,
    string? ClosedPeriodId)
{
    /// <summary>What the relief did with the part (a disposal unless set).</summary>
    public AccountingLotReliefKind Kind { get; init; }

    /// <summary>Whether the relief realized a gain or loss (a disposal).</summary>
    public bool IsDisposal => Kind == AccountingLotReliefKind.Disposal;
}