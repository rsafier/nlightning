namespace NLightning.Domain.Accounting.Financial.Lots;

using Reports;

/// <summary>
/// What an entry takes from one lot (D-A12; NL-602 A3-T4, NL-657): the msat and the lot's cost of them (null when the lot
/// has no cost in the book's currency: the gain is pending valuation), what the entry did with them and, for a disposal,
/// what it fetched for them.
/// </summary>
/// <param name="LotId">The lot (0 for the part of a disposal no lot covered, a shortfall).</param>
/// <param name="Msat">The amount taken, msat.</param>
/// <param name="Cost">The lot's cost of <paramref name="Msat"/>, or null.</param>
public sealed record FinancialLotTake(long LotId, long Msat, decimal? Cost)
{
    /// <summary>The disposal's proceeds of <see cref="Msat"/>, or null while the disposal is unvalued (a move or a
    /// settlement: its cost).</summary>
    public decimal? Proceeds { get; init; }

    /// <summary>What the entry did with the part (a disposal unless set).</summary>
    public AccountingLotReliefKind Kind { get; init; }

    /// <summary>For a move, the bucket the part went to.</summary>
    public AccountingLotBucket? ToBucket { get; init; }
}

/// <summary>
/// The open cost-basis lots of the financial book in memory (NL-602 A3-T4, D-A12), per bucket (NL-657): the lots each
/// bucket holds in the cost-basis method's order, the lots of no bucket (a node-wide pool of an older book, imported
/// lots not taken yet) and the buckets' debts. The financial projector keeps it in step with what it stages.
/// </summary>
/// <remarks>
/// <para><b>Order.</b> FIFO: acquisition time (<see cref="AccountingLot.HeldSinceOrAcquired"/>: a moved part keeps the
/// time of the lot it came from), then id; LIFO: the reverse; HIFO: cost per msat, highest first (lots without a cost in
/// the book's currency last, oldest first), then acquisition time and id. A take of an entry takes first the lots the
/// book held at or before its own time (<see cref="AccountingLot.AcquiredAt"/>) in that order, then (only to cover the
/// rest) the lots that came later (a late fact's adjustment dated after later entries).</para>
/// <para><b>Cost of a take</b> (<see cref="CostOf"/>): the lot's cost of everything relieved up to the end of the take
/// minus its cost of everything relieved before it, each rounded to 8 places, so the takes of a lot add up to its cost
/// exactly once it is used up.</para>
/// <para><b>Debts</b> (<see cref="AccountingLotOrigin.Debt"/>) are kept apart, per debtor and lender, in id order; they
/// are never taken as lots and never counted in <see cref="RemainingMsat"/>.</para>
/// </remarks>
public sealed class FinancialLotPool
{
    // The bucket key of the lots without a bucket
    private const int NoBucket = 0;

    private readonly Dictionary<long, AccountingLot> _byId = [];
    private readonly SortedSet<AccountingLot> _ordered;
    private readonly Dictionary<int, SortedSet<AccountingLot>> _byBucket = [];
    private readonly Dictionary<(int Debtor, int Lender), SortedSet<AccountingLot>> _debts = [];
    private readonly IComparer<AccountingLot> _order;

    /// <summary>A pool of <paramref name="lots"/> (those with nothing left are left out).</summary>
    public FinancialLotPool(IEnumerable<AccountingLot> lots, AccountingCostBasisMethod method, string currency)
    {
        ArgumentNullException.ThrowIfNull(lots);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        Method = method;
        Currency = currency;
        _order = new LotOrder(method, currency);
        _ordered = new SortedSet<AccountingLot>(_order);
        foreach (var lot in lots)
            Add(lot);
    }

    public AccountingCostBasisMethod Method { get; }

    /// <summary>The book's currency: a lot's cost counts only in it.</summary>
    public string Currency { get; }

    /// <summary>The msat left in every open lot (debts left out).</summary>
    public long RemainingMsat { get; private set; }

    /// <summary>The open lots of every bucket, in the method's order (debts left out).</summary>
    public IReadOnlyCollection<AccountingLot> OpenLots => _ordered;

    /// <summary>How many open lots and debts the pool holds.</summary>
    public int Count => _byId.Count;

    /// <summary>The open lot or debt, or null.</summary>
    public AccountingLot? Find(long id) => _byId.GetValueOrDefault(id);

    /// <summary>The fingerprint of the open lots and debts the pool holds (NL-658), comparable with the saved ones'
    /// (<see cref="IAccountingLotDbRepository.GetOpenLotsFingerprintAsync"/>).</summary>
    public AccountingLotsFingerprint Fingerprint =>
        new(_byId.Count, _byId.Count == 0 ? 0 : _byId.Keys.Max(), _byId.Values.Sum(l => l.RemainingMsat),
            _byId.Values.Count(l => l.ClosedPeriodId is not null));

    /// <summary>The open lots <paramref name="bucket"/> holds (null: the lots of no bucket), in the method's order.</summary>
    public IReadOnlyCollection<AccountingLot> LotsOf(AccountingLotBucket? bucket) =>
        _byBucket.TryGetValue(Key(bucket), out var lots) ? lots : [];

    /// <summary>The msat <paramref name="bucket"/>'s open lots hold.</summary>
    public long HeldMsat(AccountingLotBucket? bucket) => LotsOf(bucket).Sum(l => l.RemainingMsat);

    /// <summary>The open debts of <paramref name="debtor"/> to <paramref name="lender"/>, oldest first.</summary>
    public IReadOnlyCollection<AccountingLot> DebtsOf(AccountingLotBucket debtor, AccountingLotBucket lender) =>
        _debts.TryGetValue(((int)debtor, (int)lender), out var debts) ? debts : [];

    /// <summary>Every open debt, by id.</summary>
    public IEnumerable<AccountingLot> Debts => _debts.Values.SelectMany(d => d).OrderBy(d => d.Id);

    /// <summary>Adds an open lot or debt (one with nothing left is ignored).</summary>
    /// <exception cref="ArgumentException">The lot is already in the pool, has no id, or is a debt without its two
    /// buckets.</exception>
    public void Add(AccountingLot lot)
    {
        ArgumentNullException.ThrowIfNull(lot);
        if (lot.RemainingMsat <= 0)
            return;
        if (lot.Id <= 0)
            throw new ArgumentException("A pooled lot needs its storage id", nameof(lot));
        if (lot.IsDebt && (lot.Bucket is null || lot.Lender is null))
            throw new ArgumentException($"Debt {lot.Id} needs its debtor and lender buckets", nameof(lot));
        if (!_byId.TryAdd(lot.Id, lot))
            throw new ArgumentException($"Lot {lot.Id} is already in the pool", nameof(lot));

        Insert(lot);
    }

    /// <summary>
    /// The lots a disposal of <paramref name="msat"/> at <paramref name="at"/> would relieve from every bucket together,
    /// in order, with the cost of each take; the pool is not changed (<see cref="Relieve"/> applies a take). What no lot
    /// covers is <paramref name="shortfallMsat"/>.
    /// </summary>
    /// <param name="msat">The msat disposed of.</param>
    /// <param name="at">The disposal's time.</param>
    /// <param name="shortfallMsat">What no lot covers.</param>
    /// <param name="preferred">When set, the lots it accepts are taken first (in the same order), the others only for
    /// what they do not cover.</param>
    public IReadOnlyList<FinancialLotTake> PlanRelief(long msat, DateTimeOffset at, out long shortfallMsat,
                                                      Func<AccountingLot, bool>? preferred = null) =>
        PlanTakes(_ordered, msat, at, null, out shortfallMsat, preferred);

    /// <summary>
    /// The lots <paramref name="bucket"/> would give for <paramref name="msat"/> at <paramref name="at"/> (see
    /// <see cref="PlanRelief"/>), past what <paramref name="taken"/> already took from each lot in the same entry; the
    /// pool is not changed.
    /// </summary>
    public IReadOnlyList<FinancialLotTake> PlanBucket(AccountingLotBucket? bucket, long msat, DateTimeOffset at,
                                                      IReadOnlyDictionary<long, long>? taken, out long shortfallMsat,
                                                      Func<AccountingLot, bool>? preferred = null) =>
        PlanTakes(LotsOf(bucket), msat, at, taken, out shortfallMsat, preferred);

    /// <summary>Takes <paramref name="msat"/> from the lot or debt; returns it as it is now (removed from the pool when
    /// nothing is left).</summary>
    /// <exception cref="InvalidOperationException">The lot is not open or holds less.</exception>
    public AccountingLot Relieve(long lotId, long msat)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(msat);
        if (!_byId.TryGetValue(lotId, out var lot))
            throw new InvalidOperationException($"Lot {lotId} is not open");
        if (lot.RemainingMsat < msat)
            throw new InvalidOperationException($"Lot {lotId} holds {lot.RemainingMsat} msat, not {msat}");

        Remove(lot);
        var relieved = lot with { RemainingMsat = lot.RemainingMsat - msat };
        if (relieved.RemainingMsat > 0)
        {
            _byId[lotId] = relieved;
            Insert(relieved);
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
    public static decimal? CostOf(AccountingLot lot, long msat, string currency) => CostOf(lot, 0, msat, currency);

    /// <summary>The cost of <paramref name="msat"/> taken from <paramref name="lot"/> after <paramref name="takenMsat"/>
    /// was taken from it in the same entry, or null.</summary>
    public static decimal? CostOf(AccountingLot lot, long takenMsat, long msat, string currency)
    {
        ArgumentNullException.ThrowIfNull(lot);
        if (lot.FiatCost is not { } cost || !string.Equals(lot.FiatCurrency, currency, StringComparison.Ordinal)
                                         || lot.OriginalMsat <= 0)
            return null;

        var relievedBefore = lot.OriginalMsat - lot.RemainingMsat + takenMsat;
        return CostUpTo(cost, relievedBefore + msat, lot.OriginalMsat) - CostUpTo(cost, relievedBefore, lot.OriginalMsat);
    }

    /// <summary>A lot's cost per msat in <paramref name="currency"/>, or null.</summary>
    public static decimal? UnitCost(AccountingLot lot, string currency) =>
        lot.FiatCost is { } cost && string.Equals(lot.FiatCurrency, currency, StringComparison.Ordinal)
                                 && lot.OriginalMsat > 0
            ? cost / lot.OriginalMsat
            : null;

    private static int Key(AccountingLotBucket? bucket) => bucket is { } b ? (int)b : NoBucket;

    private IReadOnlyList<FinancialLotTake> PlanTakes(IReadOnlyCollection<AccountingLot> lots, long msat,
                                                      DateTimeOffset at, IReadOnlyDictionary<long, long>? taken,
                                                      out long shortfallMsat, Func<AccountingLot, bool>? preferred)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(msat);
        var takes = new List<FinancialLotTake>();
        var left = msat;
        if (left > 0 && lots.Count > 0)
        {
            // The lots held by then first; the later ones only for what they do not cover
            var order = lots.Where(l => l.AcquiredAt <= at).Concat(lots.Where(l => l.AcquiredAt > at));
            if (preferred is not null)
                order = order.Where(preferred).Concat(order.Where(l => !preferred(l)));

            foreach (var lot in order)
            {
                var already = taken?.GetValueOrDefault(lot.Id) ?? 0;
                var available = lot.RemainingMsat - already;
                if (available <= 0)
                    continue;

                var take = Math.Min(left, available);
                takes.Add(new FinancialLotTake(lot.Id, take, CostOf(lot, already, take, Currency)));
                left -= take;
                if (left == 0)
                    break;
            }
        }

        shortfallMsat = left;
        return takes;
    }

    private void Insert(AccountingLot lot)
    {
        if (lot.IsDebt)
        {
            var key = ((int)lot.Bucket!.Value, (int)lot.Lender!.Value);
            if (!_debts.TryGetValue(key, out var debts))
                _debts[key] = debts = new SortedSet<AccountingLot>(Comparer<AccountingLot>.Create((x, y) => x.Id.CompareTo(y.Id)));
            debts.Add(lot);
            return;
        }

        _ordered.Add(lot);
        var bucketKey = Key(lot.Bucket);
        if (!_byBucket.TryGetValue(bucketKey, out var set))
            _byBucket[bucketKey] = set = new SortedSet<AccountingLot>(_order);
        set.Add(lot);
        RemainingMsat = checked(RemainingMsat + lot.RemainingMsat);
    }

    private void Remove(AccountingLot lot)
    {
        if (lot.IsDebt)
        {
            _debts[((int)lot.Bucket!.Value, (int)lot.Lender!.Value)].Remove(lot);
            return;
        }

        _ordered.Remove(lot);
        _byBucket[Key(lot.Bucket)].Remove(lot);
        RemainingMsat -= lot.RemainingMsat;
    }

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
                    var byTime = y.HeldSinceOrAcquired.CompareTo(x.HeldSinceOrAcquired);
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

            var fifo = x.HeldSinceOrAcquired.CompareTo(y.HeldSinceOrAcquired);
            return fifo != 0 ? fifo : x.Id.CompareTo(y.Id);
        }
    }
}