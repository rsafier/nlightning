namespace NLightning.Domain.Accounting.Financial.Lots;

using Reports;

/// <summary>
/// What a disposal takes from one lot (D-A12; NL-602 A3-T4): the msat and the lot's cost of them (null when the lot has
/// no cost in the book's currency: the gain is pending valuation), and what the disposal fetched for them.
/// </summary>
/// <param name="LotId">The lot (0 for the part of a disposal no lot covered, a shortfall).</param>
/// <param name="Msat">The amount taken, msat.</param>
/// <param name="Cost">The lot's cost of <paramref name="Msat"/>, or null.</param>
public sealed record FinancialLotTake(long LotId, long Msat, decimal? Cost)
{
    /// <summary>The disposal's proceeds of <see cref="Msat"/>, or null while the disposal is unvalued.</summary>
    public decimal? Proceeds { get; init; }
}

/// <summary>
/// The open cost-basis lots of the financial book in memory (NL-602 A3-T4, D-A12): one node-wide pool (lots are not
/// tracked per bucket, so a transfer between our own buckets leaves them alone), ordered by the cost-basis method. The
/// financial projector loads it once per round from the database and keeps it in step with what it stages.
/// </summary>
/// <remarks>
/// <para><b>Order.</b> FIFO: acquisition time, then id; LIFO: the reverse; HIFO: cost per msat, highest first (lots
/// without a cost in the book's currency last, oldest first), then acquisition time and id. A disposal takes first the
/// lots acquired at or before its own time in that order, then (only to cover the rest) the lots acquired after it (a
/// late fact's adjustment dated after later entries).</para>
/// <para><b>Cost of a take</b> (<see cref="CostOf"/>): the lot's cost of everything relieved up to the end of the take
/// minus its cost of everything relieved before it, each rounded to 8 places, so the takes of a lot add up to its cost
/// exactly once it is used up.</para>
/// </remarks>
public sealed class FinancialLotPool
{
    private readonly Dictionary<long, AccountingLot> _byId = [];
    private readonly SortedSet<AccountingLot> _ordered;

    /// <summary>A pool of <paramref name="lots"/> (those with nothing left are left out).</summary>
    public FinancialLotPool(IEnumerable<AccountingLot> lots, AccountingCostBasisMethod method, string currency)
    {
        ArgumentNullException.ThrowIfNull(lots);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        Method = method;
        Currency = currency;
        _ordered = new SortedSet<AccountingLot>(new LotOrder(method, currency));
        foreach (var lot in lots)
            Add(lot);
    }

    public AccountingCostBasisMethod Method { get; }

    /// <summary>The book's currency: a lot's cost counts only in it.</summary>
    public string Currency { get; }

    /// <summary>The msat left in every open lot.</summary>
    public long RemainingMsat { get; private set; }

    /// <summary>The open lots, in the method's order.</summary>
    public IReadOnlyCollection<AccountingLot> OpenLots => _ordered;

    /// <summary>The open lot, or null.</summary>
    public AccountingLot? Find(long id) => _byId.GetValueOrDefault(id);

    /// <summary>Adds an open lot (a lot with nothing left is ignored).</summary>
    /// <exception cref="ArgumentException">The lot is already in the pool, or has no id.</exception>
    public void Add(AccountingLot lot)
    {
        ArgumentNullException.ThrowIfNull(lot);
        if (lot.RemainingMsat <= 0)
            return;
        if (lot.Id <= 0)
            throw new ArgumentException("A pooled lot needs its storage id", nameof(lot));
        if (!_byId.TryAdd(lot.Id, lot))
            throw new ArgumentException($"Lot {lot.Id} is already in the pool", nameof(lot));

        _ordered.Add(lot);
        RemainingMsat = checked(RemainingMsat + lot.RemainingMsat);
    }

    /// <summary>
    /// The lots a disposal of <paramref name="msat"/> at <paramref name="at"/> would relieve, in order, with the cost of
    /// each take; the pool is not changed (<see cref="Relieve"/> applies a take). What no lot covers is
    /// <paramref name="shortfallMsat"/>.
    /// </summary>
    public IReadOnlyList<FinancialLotTake> PlanRelief(long msat, DateTimeOffset at, out long shortfallMsat)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(msat);
        var takes = new List<FinancialLotTake>();
        var left = msat;
        if (left > 0)
        {
            // The lots acquired by then first; the later ones only for what they do not cover
            foreach (var lot in _ordered.Where(l => l.AcquiredAt <= at).Concat(_ordered.Where(l => l.AcquiredAt > at)))
            {
                var take = Math.Min(left, lot.RemainingMsat);
                takes.Add(new FinancialLotTake(lot.Id, take, CostOf(lot, take, Currency)));
                left -= take;
                if (left == 0)
                    break;
            }
        }

        shortfallMsat = left;
        return takes;
    }

    /// <summary>Takes <paramref name="msat"/> from the lot; returns the lot as it is now (removed from the pool when
    /// nothing is left).</summary>
    /// <exception cref="InvalidOperationException">The lot is not open or holds less.</exception>
    public AccountingLot Relieve(long lotId, long msat)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(msat);
        if (!_byId.TryGetValue(lotId, out var lot))
            throw new InvalidOperationException($"Lot {lotId} is not open");
        if (lot.RemainingMsat < msat)
            throw new InvalidOperationException($"Lot {lotId} holds {lot.RemainingMsat} msat, not {msat}");

        _ordered.Remove(lot);
        var relieved = lot with { RemainingMsat = lot.RemainingMsat - msat };
        RemainingMsat -= msat;
        if (relieved.RemainingMsat > 0)
        {
            _byId[lotId] = relieved;
            _ordered.Add(relieved);
        }
        else
        {
            _byId.Remove(lotId);
        }

        return relieved;
    }

    /// <summary>
    /// The cost of <paramref name="msat"/> taken from <paramref name="lot"/> as it stands (see the class remarks), or null
    /// when the lot has no cost in <paramref name="currency"/>.
    /// </summary>
    public static decimal? CostOf(AccountingLot lot, long msat, string currency)
    {
        ArgumentNullException.ThrowIfNull(lot);
        if (lot.FiatCost is not { } cost || !string.Equals(lot.FiatCurrency, currency, StringComparison.Ordinal)
                                         || lot.OriginalMsat <= 0)
            return null;

        var relievedBefore = lot.OriginalMsat - lot.RemainingMsat;
        return CostUpTo(cost, relievedBefore + msat, lot.OriginalMsat) - CostUpTo(cost, relievedBefore, lot.OriginalMsat);
    }

    /// <summary>A lot's cost per msat in <paramref name="currency"/>, or null.</summary>
    public static decimal? UnitCost(AccountingLot lot, string currency) =>
        lot.FiatCost is { } cost && string.Equals(lot.FiatCurrency, currency, StringComparison.Ordinal)
                                 && lot.OriginalMsat > 0
            ? cost / lot.OriginalMsat
            : null;

    private static decimal CostUpTo(decimal cost, long msat, long originalMsat) =>
        msat >= originalMsat ? cost : AccountingFiat.RoundStored(cost * ((decimal)msat / originalMsat));

    private sealed class LotOrder(AccountingCostBasisMethod method, string currency) : IComparer<AccountingLot>
    {
        public int Compare(AccountingLot? x, AccountingLot? y)
        {
            if (ReferenceEquals(x, y))
                return 0;
            if (x is null)
                return -1;
            if (y is null)
                return 1;

            switch (method)
            {
                case AccountingCostBasisMethod.Lifo:
                    var byTime = y.AcquiredAt.CompareTo(x.AcquiredAt);
                    return byTime != 0 ? byTime : y.Id.CompareTo(x.Id);
                case AccountingCostBasisMethod.Hifo:
                    var (xCost, yCost) = (UnitCost(x, currency), UnitCost(y, currency));
                    if (xCost is not null && yCost is null)
                        return -1;
                    if (xCost is null && yCost is not null)
                        return 1;
                    if (xCost is { } xc && yCost is { } yc && xc != yc)
                        return yc.CompareTo(xc);
                    break;
            }

            var fifo = x.AcquiredAt.CompareTo(y.AcquiredAt);
            return fifo != 0 ? fifo : x.Id.CompareTo(y.Id);
        }
    }
}