namespace NLightning.Domain.Accounting.Financial.Lots;

using Books;
using Classification;
using Prices;
using Reports;

/// <summary>What the financial projector hands the <see cref="FinancialEntryPlanner"/> for one entry.</summary>
/// <param name="Lines">The entry's financial lines (every one names its account; a line may come valued already, a
/// reversal's copy of its original; lines without an amount are dropped).</param>
/// <param name="Price">The price that values the entry (D-A11), or null when none is usable yet.</param>
/// <param name="At">The time the lots are relieved at (the entry's, or its adjustment's).</param>
public sealed record FinancialEntryPlanInput(IReadOnlyList<AccountingPosting> Lines, AccountingPrice? Price,
                                            DateTimeOffset At)
{
    /// <summary>An acquisition opens an opening-balance lot at an estimated basis (D-A9) rather than an acquisition
    /// lot.</summary>
    public bool IsOpeningBalance { get; init; }

    /// <summary>The opening balances' lots were imported (D-A9): an opening balance takes the imported lots (the lots of
    /// no bucket) into its bucket at their cost instead of opening a lot.</summary>
    public bool OpeningLotsImported { get; init; }

    /// <summary>
    /// The fact and its reversal are both in the open period (a reorg): neither touches the lots, and the entry is only
    /// valued (its rounding balanced on the cost-basis line), so the pair nets to zero in msat and in fiat.
    /// </summary>
    public bool Cancelled { get; init; }

    /// <summary>
    /// The ledger sequence of the closed period's fact this entry takes back (its reversal, NL-675): its disposals are
    /// corrections, not sales: they relieve first the lot that fact acquired, at cost, and realize nothing.
    /// </summary>
    public long? CorrectionOf { get; init; }
}

/// <summary>A lot an entry opens (its id comes from the repository): an acquisition, a part moved from another lot
/// (<see cref="ParentLotId"/>) or a bucket's debt (<see cref="AccountingLotOrigin.Debt"/>).</summary>
public sealed record FinancialLotSpec(
    long Msat,
    decimal? Cost,
    string? Currency,
    long? PriceId,
    AccountingLotOrigin Origin,
    bool BasisEstimated)
{
    /// <summary>The bucket that holds it (the debtor of a debt).</summary>
    public AccountingLotBucket? Bucket { get; init; }

    /// <summary>The lot a moved part came from.</summary>
    public long? ParentLotId { get; init; }

    /// <summary>The acquisition time a moved part keeps.</summary>
    public DateTimeOffset? HeldSince { get; init; }

    /// <summary>The bucket a debt is owed to.</summary>
    public AccountingLotBucket? Lender { get; init; }
}

/// <summary>
/// The financial entry the planner works out (NL-602 A3-T4, NL-657): its lines (valued, with the realized gain or loss),
/// its flags, the parts of lots it takes (disposals, moves, debt settlements) and the lots it opens (acquisitions, moved
/// parts, debts).
/// </summary>
public sealed record FinancialEntryPlan(
    IReadOnlyList<AccountingPosting> Postings,
    AccountingEntryFlags Flags,
    IReadOnlyList<FinancialLotTake> Reliefs,
    IReadOnlyList<FinancialLotSpec> NewLots)
{
    /// <summary>The msat that no lot covered (valued at market; a disposal's cost is taken as its proceeds).</summary>
    public long ShortfallMsat { get; init; }

    /// <summary>The realized gain (negative: a loss) of the entry's disposals, or null when there is none or it is
    /// pending valuation.</summary>
    public decimal? RealizedGain { get; init; }

    /// <summary>A note for the entry (a shortfall, sats back beyond what is held outside), or null.</summary>
    public string? Note { get; init; }

    /// <summary>The entry's fiat sum (0 for a valued entry, by construction).</summary>
    public decimal FiatSum => Postings.Sum(p => p.FiatAmount ?? 0m);

    /// <summary>The lots the entry acquires (acquisitions and opening balances, not moved parts or debts).</summary>
    public IEnumerable<FinancialLotSpec> Acquired =>
        NewLots.Where(l => l.ParentLotId is null && l.Origin != AccountingLotOrigin.Debt);

    /// <summary>The parts of lots the entry moved to other buckets.</summary>
    public IEnumerable<FinancialLotSpec> Moved => NewLots.Where(l => l.ParentLotId is not null);

    /// <summary>The debts the entry opens.</summary>
    public IEnumerable<FinancialLotSpec> Debts => NewLots.Where(l => l.Origin == AccountingLotOrigin.Debt);

    /// <summary>The disposals among <see cref="Reliefs"/>.</summary>
    public IEnumerable<FinancialLotTake> Disposals => Reliefs.Where(r => r.Kind == AccountingLotReliefKind.Disposal);
}

/// <summary>
/// The cost-basis rules of the financial book for one entry (NL-602 A3-T4, NL-657, NL-674, NL-675; plan
/// <c>docs/agents/ACCOUNTING_PLAN.md</c> §6.2, D-A9, D-A12). Pure: the lines, the price and the open lots in, the plan
/// out; the pool is not changed.
/// </summary>
/// <remarks>
/// <para><b>Flows.</b> Each bucket line (<see cref="FinancialLotRules.BucketOf"/>: our asset buckets, the rebalance in
/// transit, the sats held outside the node) adds to its bucket's net flow; the debits and credits of one bucket in the
/// same entry net out (their common part stays). The buckets that give (a net credit) and the acquisitions are the
/// sources; the disposals (in line order) and then the buckets that receive (a net debit) are the demands. Each demand
/// draws from the sources in order (the buckets first, the acquisitions last): a disposal takes the source bucket's lots
/// in the method's order and realizes proceeds − cost; a bucket takes them as moved parts at their cost (a relief of
/// kind <see cref="AccountingLotReliefKind.Move"/> and a new lot of the destination that keeps the part's cost, origin
/// and acquisition time); an acquisition opens one lot per receiving bucket at the market value.</para>
/// <para><b>A bucket short of lots</b> (the clearing account spent before the wallet's own event, a rebalance received
/// before it was paid): a debt the destination owes the source is settled first (no lot moves; valued at the debt's
/// cost); then the source's own lots; then the lots of no bucket (an older book's node-wide pool, imported lots left),
/// taken over without a debt; then the destination's own lots as a claim (nothing moves: the source owes the destination,
/// valued at the cost of the destination's lots in the method's order); then the other buckets' lots in the source's
/// order (<see cref="LendersOf"/>), moved or disposed of, the source owing the lender. The sats held outside the node
/// never lend: a deposit classified as a transfer back beyond what is held outside acquires the rest at market. What no
/// lot covers is a shortfall: valued at market (a disposal's cost is taken as its proceeds) and noted.</para>
/// <para><b>Valuation.</b> The acquisition lines carry their market value (<see cref="AccountingValuation.FiatValue"/>
/// at the entry's price), the disposal lines their proceeds (market value; 0 for a loss, D-A12; the cost relieved for a
/// correction), and every bucket line the cost of the lots it moved (the common part of a bucket's debits and credits at
/// market), so each asset account carries its own cost basis (NL-657) and the gain or loss is a fiat-only line
/// (<c>income:gains:realized</c> credited, or <c>expenses:losses:realized</c> debited). Without a usable price an entry
/// is still exact when it needs no market value (a transfer of lots that have a cost, a loss); otherwise it is
/// <see cref="AccountingEntryFlags.Unvalued"/> and <see cref="AccountingEntryFlags.PendingValuation"/>, no line keeps a
/// value (a line valued already, a reversal's copy, keeps its value only when every line has one), and the projector
/// projects it again once its price is known. With a price but a part of a lot without a cost, the lines keep their
/// market values, the gain is pending (<see cref="AccountingEntryFlags.GainPending"/>, never zero) and the rounding is
/// balanced on a fiat-only <see cref="FinancialAccount.CostBasis"/> line.</para>
/// <para><b>Corrections</b> realize nothing: a debit of the opening balances (NL-673) relieves the opening and imported
/// lots first, and a closed fact's reversal (<see cref="FinancialEntryPlanInput.CorrectionOf"/>, NL-675) the lot that
/// fact acquired first; when every part has a cost (and none is a shortfall) the proceeds are that cost.</para>
/// </remarks>
public static class FinancialEntryPlanner
{
    /// <summary>The buckets a bucket borrows lots from, in order, when it gives more than it holds (after the lots of no
    /// bucket and the destination's own; <see cref="AccountingLotBucket.HeldOutside"/> never lends nor borrows).</summary>
    public static IReadOnlyList<AccountingLotBucket> LendersOf(AccountingLotBucket bucket) => bucket switch
    {
        AccountingLotBucket.Clearing => [AccountingLotBucket.Wallet, AccountingLotBucket.Channels,
                                         AccountingLotBucket.Pending, AccountingLotBucket.Rebalance],
        AccountingLotBucket.Wallet => [AccountingLotBucket.Clearing, AccountingLotBucket.Channels,
                                       AccountingLotBucket.Pending, AccountingLotBucket.Rebalance],
        AccountingLotBucket.Channels => [AccountingLotBucket.Rebalance, AccountingLotBucket.Clearing,
                                         AccountingLotBucket.Wallet, AccountingLotBucket.Pending],
        AccountingLotBucket.Pending => [AccountingLotBucket.Channels, AccountingLotBucket.Clearing,
                                        AccountingLotBucket.Wallet, AccountingLotBucket.Rebalance],
        AccountingLotBucket.Rebalance => [AccountingLotBucket.Channels, AccountingLotBucket.Clearing,
                                          AccountingLotBucket.Wallet, AccountingLotBucket.Pending],
        _ => []
    };

    /// <summary>Works out the entry (see the class remarks).</summary>
    /// <param name="input">The entry's lines, price and time.</param>
    /// <param name="pool">The open lots (not changed).</param>
    /// <param name="chart">The financial chart (the gain, loss and cost-basis accounts, the transfer accounts).</param>
    public static FinancialEntryPlan Plan(FinancialEntryPlanInput input, FinancialLotPool pool, FinancialChart chart)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(chart);

        var lines = input.Lines.Where(l => l.AmountMsat != 0).ToList();
        if (lines.Count == 0)
            return new FinancialEntryPlan([], AccountingEntryFlags.None, [], []);

        return new Planner(input, pool, chart, lines).Run();
    }

    /// <summary>The asset role of a line of the entry's own (the largest asset line's, else the clearing
    /// account's).</summary>
    public static AccountRole PrimaryAssetRole(IEnumerable<AccountingPosting> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        AccountRole? role = null;
        var largest = -1L;
        foreach (var line in lines)
        {
            if (!FinancialLotRules.IsAsset(line.Account))
                continue;

            var size = line.AmountMsat == long.MinValue ? long.MaxValue : Math.Abs(line.AmountMsat);
            if (size <= largest)
                continue;

            largest = size;
            role = line.Account;
        }

        return role ?? AccountRole.Clearing;
    }

    private static decimal? Add(decimal? left, decimal? right) => left is { } l && right is { } r ? l + r : null;

    private static AccountingPosting FiatLine(AccountRole role, string accountName, decimal fiat, string currency) =>
        new(role, 0) { AccountName = accountName, FiatAmount = fiat, FiatCurrency = currency };

    /// <summary>The work of one plan.</summary>
    private sealed class Planner
    {
        private readonly FinancialEntryPlanInput _input;
        private readonly FinancialLotPool _pool;
        private readonly FinancialChart _chart;
        private readonly List<AccountingPosting> _lines;
        private readonly string _currency;
        private readonly FinancialLineKind[] _kinds;
        private readonly AccountingLotBucket?[] _buckets;
        private readonly decimal?[] _market;
        private readonly bool _valued;

        // What the entry already took from each lot and debt (the pool is not changed while planning)
        private readonly Dictionary<long, long> _taken = [];
        private readonly List<FinancialLotTake> _reliefs = [];
        private readonly List<FinancialLotSpec> _newLots = [];

        // The new lots merged per moved parent and destination, per acquiring bucket and per debt
        private readonly Dictionary<(long Parent, AccountingLotBucket To), int> _moved = [];
        private readonly Dictionary<AccountingLotBucket, int> _acquiredInto = [];
        private readonly Dictionary<(AccountingLotBucket Debtor, AccountingLotBucket Lender), int> _owed = [];

        // Per bucket the cost of what came in and went out; per disposal line its parts
        private readonly Dictionary<AccountingLotBucket, Flow> _flows = [];
        private readonly Dictionary<int, Disposed> _disposed = [];

        private long _shortfall;
        private long _heldOutsideExcess;
        private decimal? _openingValue = 0m;

        public Planner(FinancialEntryPlanInput input, FinancialLotPool pool, FinancialChart chart,
                       List<AccountingPosting> lines)
        {
            _input = input;
            _pool = pool;
            _chart = chart;
            _lines = lines;
            _currency = pool.Currency;
            _kinds = lines.Select(l => FinancialLotRules.KindOf(l, chart)).ToArray();
            _buckets = lines.Select(l => FinancialLotRules.BucketOf(l, chart)).ToArray();
            _market = new decimal?[lines.Count];
            _valued = Valuate();
        }

        private bool OpeningFromImports => _input.IsOpeningBalance && _input.OpeningLotsImported;

        public FinancialEntryPlan Run()
        {
            var flags = _valued
                            ? AccountingEntryFlags.None
                            : AccountingEntryFlags.Unvalued | AccountingEntryFlags.PendingValuation;
            if (_input.Cancelled)
                return Cancelled(flags);

            Allocate();
            return Value(flags);
        }

        #region Market values

        // The market value of every line (or the value it came with); false when one cannot be valued
        private bool Valuate()
        {
            var preset = _lines.All(l => l.FiatAmount is not null
                                      && string.Equals(l.FiatCurrency, _currency, StringComparison.Ordinal));
            if (preset)
            {
                for (var i = 0; i < _lines.Count; i++)
                    _market[i] = _lines[i].FiatAmount;
                return true;
            }

            var price = _input.Price is { } p && string.Equals(p.Currency, _currency, StringComparison.Ordinal)
                            ? p
                            : null;
            for (var i = 0; i < _lines.Count; i++)
            {
                // A loss fetches nothing, priced or not (D-A12)
                if (_kinds[i] == FinancialLineKind.Disposal && FinancialLotRules.HasZeroProceeds(_lines[i].Account))
                    _market[i] = 0m;
                else
                    _market[i] = price is null ? null : AccountingValuation.FiatValue(_lines[i].AmountMsat, price.Price);
            }

            return price is not null;
        }

        // The market value of msat moved without a lot (a shortfall), or null without a price
        private decimal? MarketOf(long msat)
        {
            if (_input.Price is { } price && string.Equals(price.Currency, _currency, StringComparison.Ordinal))
                return AccountingValuation.FiatValue(msat, price.Price);

            // A reversal's copy of valued lines: the value per msat of its first valued line that is not a loss
            for (var i = 0; i < _lines.Count; i++)
            {
                if (_valued && _market[i] is { } value && value != 0m)
                    return AccountingFiat.RoundStored(value / _lines[i].AmountMsat * msat);
            }

            return null;
        }

        #endregion

        #region Allocation

        private void Allocate()
        {
            // The net flow of each bucket, in the order of their first line
            var order = new List<AccountingLotBucket>();
            var net = new Dictionary<AccountingLotBucket, long>();
            for (var i = 0; i < _lines.Count; i++)
            {
                if (_buckets[i] is not { } bucket)
                    continue;

                if (net.TryAdd(bucket, 0))
                    order.Add(bucket);
                net[bucket] = checked(net[bucket] + _lines[i].AmountMsat);
            }

            // Sources: the giving buckets, then the acquisitions (the imported lots for an opening balance after an
            // import); demands: the disposals, then the receiving buckets
            var sources = new List<Source>();
            foreach (var bucket in order.Where(b => net[b] < 0))
                sources.Add(new Source(SourceKind.Bucket, bucket, -1, -net[bucket]));
            for (var i = 0; i < _lines.Count; i++)
            {
                if (_kinds[i] == FinancialLineKind.Acquisition)
                    sources.Add(new Source(OpeningFromImports ? SourceKind.Imported : SourceKind.Acquisition, null, i,
                                           -_lines[i].AmountMsat)
                    {
                        Share = new Sharer(_market[i] is { } v ? -v : null, -_lines[i].AmountMsat)
                    });
            }

            var demands = new List<Demand>();
            for (var i = 0; i < _lines.Count; i++)
            {
                if (_kinds[i] == FinancialLineKind.Disposal)
                    demands.Add(new Demand(null, i, _lines[i].AmountMsat));
            }

            foreach (var bucket in order.Where(b => net[b] > 0))
                demands.Add(new Demand(bucket, -1, net[bucket]));

            foreach (var demand in demands)
            {
                var left = demand.Msat;
                foreach (var source in sources)
                {
                    if (left == 0)
                        break;
                    if (source.Left == 0)
                        continue;

                    var msat = Math.Min(left, source.Left);
                    source.Left -= msat;
                    left -= msat;
                    switch (source.Kind)
                    {
                        case SourceKind.Bucket:
                            FromBucket(source.Bucket!.Value, demand, msat);
                            break;
                        case SourceKind.Imported:
                            FromImported(demand, msat);
                            break;
                        default:
                            FromAcquisition(demand, msat, source.Share!.Next(msat));
                            break;
                    }
                }

                if (left != 0)
                    throw new InvalidOperationException("The entry does not balance in msat");
            }
        }

        // A giving bucket's msat to a demand, in the order of the class remarks
        private void FromBucket(AccountingLotBucket source, Demand demand, long msat)
        {
            var left = msat;
            if (demand.Bucket is { } destination)
                left -= Settle(destination, source, left);

            var preferred = demand.IsDisposal ? CorrectionPreference(demand.Line) : null;
            left -= Take(source, source, demand, left, preferred).Msat;
            if (left == 0)
                return;

            if (source == AccountingLotBucket.HeldOutside)
            {
                // Sats back from outside beyond what is held there: acquired at market
                _heldOutsideExcess += left;
                var market = MarketOf(left);
                Out(source, left, market);
                FromAcquisition(demand, left, market);
                return;
            }

            // The lots of no bucket are taken over, without a debt
            left -= Take(null, source, demand, left, preferred).Msat;

            // The destination's own lots: a claim on the source, nothing moves
            if (left > 0 && demand.Bucket is { } claimant && claimant != AccountingLotBucket.HeldOutside)
                left -= Claim(source, claimant, left);

            foreach (var lender in LendersOf(source))
            {
                if (left == 0)
                    break;
                if (lender == demand.Bucket)
                    continue;

                var (lent, cost) = Take(lender, source, demand, left, null);
                if (lent > 0)
                    AddDebt(source, lender, lent, cost);
                left -= lent;
            }

            if (left > 0)
                Shortfall(source, demand, left);
        }

        // Takes msat of the holder's lots for the demand (moved parts or disposals), on the source's account
        private (long Msat, decimal? Cost) Take(AccountingLotBucket? holder, AccountingLotBucket? source, Demand demand,
                                                long msat, Func<AccountingLot, bool>? preferred)
        {
            if (msat == 0)
                return (0, 0m);

            var takes = _pool.PlanBucket(holder, msat, _input.At, _taken, out var rest, preferred);
            decimal? cost = 0m;
            foreach (var take in takes)
            {
                var lot = _pool.Find(take.LotId)!;
                var before = _taken.GetValueOrDefault(take.LotId);
                _taken[take.LotId] = before + take.Msat;
                cost = Add(cost, take.Cost);
                if (demand.Bucket is { } destination)
                {
                    _reliefs.Add(take with
                    {
                        Kind = AccountingLotReliefKind.Move,
                        ToBucket = destination,
                        Proceeds = take.Cost
                    });
                    AddMoved(lot, before, take.Msat, destination);
                }
                else
                {
                    _reliefs.Add(take);
                    DisposedOf(demand.Line).Reliefs.Add(_reliefs.Count - 1);
                }
            }

            var took = msat - rest;
            if (took == 0)
                return (0, 0m);

            if (source is { } from)
                Out(from, took, cost);
            Into(demand, took, cost);
            return (took, cost);
        }

        // A debt of the debtor to the lender settled by msat coming back from the lender; returns what it settled
        private long Settle(AccountingLotBucket debtor, AccountingLotBucket lender, long msat)
        {
            var left = msat;
            decimal? cost = 0m;
            foreach (var debt in _pool.DebtsOf(debtor, lender))
            {
                if (left == 0)
                    break;

                var before = _taken.GetValueOrDefault(debt.Id);
                var available = debt.RemainingMsat - before;
                if (available <= 0)
                    continue;

                var settled = Math.Min(left, available);
                var part = FinancialLotPool.CostOf(debt, before, settled, _currency);
                _taken[debt.Id] = before + settled;
                _reliefs.Add(new FinancialLotTake(debt.Id, settled, part)
                {
                    Kind = AccountingLotReliefKind.Settlement,
                    Proceeds = part
                });
                cost = Add(cost, part);
                left -= settled;
            }

            var total = msat - left;
            if (total > 0)
            {
                Out(lender, total, cost);
                In(debtor, total, cost);
            }

            return total;
        }

        // The claimant lends msat of its own lots: nothing moves, the source owes it; returns what it lent
        private long Claim(AccountingLotBucket source, AccountingLotBucket claimant, long msat)
        {
            var takes = _pool.PlanBucket(claimant, msat, _input.At, _taken, out var rest);
            var lent = msat - rest;
            if (lent == 0)
                return 0;

            decimal? cost = 0m;
            foreach (var take in takes)
                cost = Add(cost, take.Cost);

            AddDebt(source, claimant, lent, cost);
            Out(source, lent, cost);
            In(claimant, lent, cost);
            return lent;
        }

        // msat no lot covers: valued at market; a disposal's cost is the proceeds of that part (set in Value)
        private void Shortfall(AccountingLotBucket source, Demand demand, long msat)
        {
            _shortfall += msat;
            if (demand.IsDisposal)
            {
                var disposed = DisposedOf(demand.Line);
                disposed.ShortfallMsat += msat;
                disposed.ShortfallSource ??= source;
                FlowOf(source).OutMsat += msat;
                disposed.Msat += msat;
                return;
            }

            var market = MarketOf(msat);
            Out(source, msat, market);
            In(demand.Bucket!.Value, msat, market);
        }

        // An opening balance after a lot import takes the imported lots (no bucket) into its bucket at their cost; an
        // import short of it opens an estimated lot at market for the rest
        private void FromImported(Demand demand, long msat)
        {
            var (took, cost) = Take(null, null, demand, msat, null);
            _openingValue = Add(_openingValue, cost);
            if (took < msat)
            {
                var market = MarketOf(msat - took);
                _openingValue = Add(_openingValue, market);
                FromAcquisition(demand, msat - took, market);
            }
        }

        // An acquisition's msat at their market value: a lot of the receiving bucket, or a disposal of what was just
        // acquired (no gain)
        private void FromAcquisition(Demand demand, long msat, decimal? value)
        {
            if (demand.Bucket is not { } destination)
            {
                var disposed = DisposedOf(demand.Line);
                disposed.Msat += msat;
                disposed.Cost = Add(disposed.Cost, value);
                return;
            }

            In(destination, msat, value);
            if (_acquiredInto.TryGetValue(destination, out var index))
            {
                var spec = _newLots[index];
                _newLots[index] = spec with { Msat = spec.Msat + msat, Cost = Add(spec.Cost, value) };
                return;
            }

            var origin = _input.IsOpeningBalance ? AccountingLotOrigin.Opening : AccountingLotOrigin.Acquisition;
            _acquiredInto[destination] = _newLots.Count;
            _newLots.Add(new FinancialLotSpec(msat, value, null, _input.Price?.Id, origin, _input.IsOpeningBalance)
            {
                Bucket = destination
            });
        }

        private void AddMoved(AccountingLot parent, long takenBefore, long msat, AccountingLotBucket destination)
        {
            // The part keeps the parent's cost in the parent's own currency
            var cost = parent.FiatCurrency is { } currency
                           ? FinancialLotPool.CostOf(parent, takenBefore, msat, currency)
                           : null;
            if (_moved.TryGetValue((parent.Id, destination), out var index))
            {
                var spec = _newLots[index];
                _newLots[index] = spec with { Msat = spec.Msat + msat, Cost = Add(spec.Cost, cost) };
                return;
            }

            _moved[(parent.Id, destination)] = _newLots.Count;
            _newLots.Add(new FinancialLotSpec(msat, cost, cost is null ? null : parent.FiatCurrency,
                                              cost is null ? null : parent.PriceId, parent.Origin,
                                              parent.BasisEstimated)
            {
                Bucket = destination,
                ParentLotId = parent.Id,
                HeldSince = parent.HeldSinceOrAcquired
            });
        }

        private void AddDebt(AccountingLotBucket debtor, AccountingLotBucket lender, long msat, decimal? cost)
        {
            if (_owed.TryGetValue((debtor, lender), out var index))
            {
                var spec = _newLots[index];
                _newLots[index] = spec with { Msat = spec.Msat + msat, Cost = Add(spec.Cost, cost) };
                return;
            }

            _owed[(debtor, lender)] = _newLots.Count;
            _newLots.Add(new FinancialLotSpec(msat, cost, null, null, AccountingLotOrigin.Debt, false)
            {
                Bucket = debtor,
                Lender = lender
            });
        }

        // A correction relieves the lots it corrects first (NL-673, NL-675)
        private Func<AccountingLot, bool>? CorrectionPreference(int line)
        {
            if (FinancialLotRules.IsOpeningCorrection(_lines[line]))
                return l => l.Origin is AccountingLotOrigin.Opening or AccountingLotOrigin.Import;

            if (_input.CorrectionOf is { } fact)
                return l => l.SourceLedgerSeq == fact && l.ParentLotId is null;

            return null;
        }

        private bool IsCorrection(int line) =>
            FinancialLotRules.IsOpeningCorrection(_lines[line]) || _input.CorrectionOf is not null;

        private void Into(Demand demand, long msat, decimal? cost)
        {
            if (demand.Bucket is { } destination)
            {
                In(destination, msat, cost);
                return;
            }

            var disposed = DisposedOf(demand.Line);
            disposed.Msat += msat;
            disposed.Cost = Add(disposed.Cost, cost);
        }

        private void Out(AccountingLotBucket bucket, long msat, decimal? cost)
        {
            var flow = FlowOf(bucket);
            flow.OutMsat += msat;
            flow.OutCost = Add(flow.OutCost, cost);
        }

        private void In(AccountingLotBucket bucket, long msat, decimal? cost)
        {
            var flow = FlowOf(bucket);
            flow.InMsat += msat;
            flow.InCost = Add(flow.InCost, cost);
        }

        private Flow FlowOf(AccountingLotBucket bucket)
        {
            if (!_flows.TryGetValue(bucket, out var flow))
                _flows[bucket] = flow = new Flow();
            return flow;
        }

        private Disposed DisposedOf(int line)
        {
            if (!_disposed.TryGetValue(line, out var disposed))
                _disposed[line] = disposed = new Disposed();
            return disposed;
        }

        #endregion

        #region Values

        private FinancialEntryPlan Value(AccountingEntryFlags flags)
        {
            // The proceeds of each disposal line: its market value (or the cost relieved for a correction), the lots'
            // part shared over its reliefs by msat; a shortfall's part is its own cost (no gain)
            decimal? gain = 0m;
            foreach (var (line, disposed) in _disposed.OrderBy(d => d.Key))
            {
                var lotMsat = disposed.Reliefs.Sum(r => _reliefs[r].Msat);
                var correction = IsCorrection(line) && disposed.Cost is not null && disposed.ShortfallMsat == 0;
                var proceeds = correction ? disposed.Cost : _market[line];
                if (proceeds is not { } value)
                {
                    // Without its proceeds a shortfall's cost is unknown too
                    if (disposed.ShortfallSource is { } unknown)
                        FlowOf(unknown).OutCost = null;
                    gain = null;
                    continue;
                }

                disposed.Proceeds = value;
                var lotsProceeds = correction || lotMsat == disposed.Msat
                                       ? value
                                       : AccountingFiat.RoundStored(value * ((decimal)lotMsat / disposed.Msat));
                var sharer = new Sharer(lotsProceeds, lotMsat);
                foreach (var index in disposed.Reliefs)
                {
                    var relief = _reliefs[index];
                    _reliefs[index] = relief with { Proceeds = correction ? relief.Cost : sharer.Next(relief.Msat) };
                }

                if (disposed.ShortfallSource is { } source)
                {
                    // The shortfall's part of the proceeds is its cost: no gain on it
                    var otherMsat = disposed.Msat - lotMsat;
                    var other = value - lotsProceeds;
                    var shortfallCost = otherMsat == disposed.ShortfallMsat
                                            ? other
                                            : AccountingFiat.RoundStored(
                                                other * ((decimal)disposed.ShortfallMsat / otherMsat));
                    disposed.Cost = Add(disposed.Cost, shortfallCost);
                    FlowOf(source).OutCost = Add(FlowOf(source).OutCost, shortfallCost);
                }

                gain = disposed.Cost is { } cost ? Add(gain, value - cost) : null;
            }

            var values = new decimal?[_lines.Count];
            for (var i = 0; i < _lines.Count; i++)
            {
                values[i] = _kinds[i] switch
                {
                    FinancialLineKind.Acquisition => _market[i],
                    FinancialLineKind.Disposal => _disposed.TryGetValue(i, out var d) ? d.Proceeds : _market[i],
                    _ => values[i]
                };
            }

            // An opening balance after an import: the opening line carries the imported cost
            if (OpeningFromImports)
                ShareOver(values, i => _kinds[i] == FinancialLineKind.Acquisition,
                          _openingValue is { } opening ? -opening : null);

            foreach (var bucket in _buckets.OfType<AccountingLotBucket>().Distinct())
                ValueBucket(bucket, values);

            if (gain is not null && values.All(v => v is not null))
                return Exact(values, gain.Value);

            return Pending(flags);
        }

        private void ValueBucket(AccountingLotBucket bucket, decimal?[] values)
        {
            var indexes = Enumerable.Range(0, _lines.Count).Where(i => _buckets[i] == bucket).ToList();
            var flow = _flows.GetValueOrDefault(bucket) ?? new Flow();
            var debits = indexes.Where(i => _lines[i].AmountMsat > 0).ToHashSet();
            var credits = indexes.Where(i => _lines[i].AmountMsat < 0).ToHashSet();
            if (flow.OutMsat > 0)
            {
                // A giving bucket: its debits stay (at market), its credits carry them and the cost of what went out
                decimal? stay = 0m;
                foreach (var i in debits)
                {
                    values[i] = _market[i];
                    stay = Add(stay, _market[i]);
                }

                var total = Add(stay, flow.OutCost) is { } sum ? -sum : (decimal?)null;
                ShareOver(values, credits.Contains, total);
            }
            else if (flow.InMsat > 0)
            {
                decimal? stay = 0m;
                foreach (var i in credits)
                {
                    values[i] = _market[i];
                    stay = Add(stay, _market[i] is { } m ? -m : null);
                }

                ShareOver(values, debits.Contains, Add(stay, flow.InCost));
            }
            else
            {
                foreach (var i in indexes)
                    values[i] = _market[i];
            }
        }

        // Shares a total over the selected lines by msat (telescoping, so the shares add up to it)
        private void ShareOver(decimal?[] values, Func<int, bool> selected, decimal? total)
        {
            var indexes = Enumerable.Range(0, _lines.Count).Where(selected).ToList();
            var sharer = new Sharer(total, indexes.Sum(i => _lines[i].AmountMsat));
            foreach (var i in indexes)
                values[i] = sharer.Next(_lines[i].AmountMsat);
        }

        // Every line valued: the bucket lines at cost, the rest at market (or proceeds), the gain on its own line
        private FinancialEntryPlan Exact(decimal?[] values, decimal gain)
        {
            var priceId = _input.Price is { } price && string.Equals(price.Currency, _currency, StringComparison.Ordinal)
                              ? price.Id
                              : (long?)null;
            var postings = new List<AccountingPosting>(_lines.Count + 1);
            for (var i = 0; i < _lines.Count; i++)
            {
                var line = _lines[i];
                var atMarket = _buckets[i] is null && values[i] == _market[i];
                postings.Add(line with
                {
                    FiatAmount = values[i],
                    FiatCurrency = _currency,
                    PriceId = atMarket ? priceId ?? line.PriceId : null
                });
            }

            var role = PrimaryAssetRole(_lines);
            if (gain > 0m)
                postings.Add(FiatLine(role, _chart[FinancialAccount.RealizedGains], -gain, _currency));
            else if (gain < 0m)
                postings.Add(FiatLine(role, _chart[FinancialAccount.RealizedLosses], -gain, _currency));

            // The shares telescope, so nothing is left; a residue would be balanced on the cost-basis line
            var residual = postings.Sum(p => p.FiatAmount ?? 0m);
            if (residual != 0m)
                postings.Add(FiatLine(role, _chart[FinancialAccount.CostBasis], -residual, _currency));

            return new FinancialEntryPlan(postings, AccountingEntryFlags.None, _reliefs, Normalize())
            {
                ShortfallMsat = _shortfall,
                RealizedGain = _disposed.Count > 0 ? gain : null,
                Note = Note()
            };
        }

        // A part without a cost or a line without a market value: the lines at market (or none without a price), the
        // gain pending, the rounding on the cost-basis line
        private FinancialEntryPlan Pending(AccountingEntryFlags flags)
        {
            var postings = new List<AccountingPosting>(_lines.Count + 1);
            for (var i = 0; i < _lines.Count; i++)
            {
                postings.Add(_lines[i] with
                {
                    FiatAmount = _valued ? _market[i] : null,
                    FiatCurrency = _valued ? _currency : null,
                    PriceId = _valued ? _input.Price?.Id ?? _lines[i].PriceId : null
                });
            }

            if (_disposed.Count > 0)
                flags |= AccountingEntryFlags.GainPending;

            if (_valued)
            {
                var residual = postings.Sum(p => p.FiatAmount ?? 0m);
                if (residual != 0m)
                    postings.Add(FiatLine(PrimaryAssetRole(_lines), _chart[FinancialAccount.CostBasis], -residual,
                                          _currency));
            }

            return new FinancialEntryPlan(postings, flags, _reliefs, Normalize())
            {
                ShortfallMsat = _shortfall,
                Note = Note()
            };
        }

        private FinancialEntryPlan Cancelled(AccountingEntryFlags flags)
        {
            var postings = _lines.Select((l, i) => l with
            {
                FiatAmount = _valued ? _market[i] : null,
                FiatCurrency = _valued ? _currency : null,
                PriceId = _valued ? _input.Price?.Id ?? l.PriceId : null
            }).ToList();
            var residual = _valued ? postings.Sum(p => p.FiatAmount ?? 0m) : 0m;
            if (residual != 0m)
                postings.Add(FiatLine(PrimaryAssetRole(_lines), _chart[FinancialAccount.CostBasis], -residual,
                                      _currency));
            return new FinancialEntryPlan(postings, flags, [], []);
        }

        private string? Note()
        {
            var notes = new List<string>();
            if (_shortfall > 0)
                notes.Add($"lot shortfall: {_shortfall} msat without a lot (valued at market, no gain)");
            if (_heldOutsideExcess > 0)
                notes.Add($"{_heldOutsideExcess} msat back beyond what is held outside the node (acquired at market)");
            return notes.Count == 0 ? null : string.Join("; ", notes);
        }

        // A new lot's currency follows its cost
        private IReadOnlyList<FinancialLotSpec> Normalize() =>
            _newLots.Select(l => l.Cost is null
                                     ? l with { Currency = null, PriceId = null }
                                     : l.Currency is null
                                         ? l with { Currency = _currency }
                                         : l).ToList();

        #endregion

        private sealed class Source(SourceKind kind, AccountingLotBucket? bucket, int line, long msat)
        {
            public SourceKind Kind { get; } = kind;
            public AccountingLotBucket? Bucket { get; } = bucket;
            public int Line { get; } = line;
            public long Left { get; set; } = msat;
            public Sharer? Share { get; init; }
        }

        private sealed record Demand(AccountingLotBucket? Bucket, int Line, long Msat)
        {
            public bool IsDisposal => Bucket is null;
        }

        private sealed class Flow
        {
            public long InMsat { get; set; }
            public decimal? InCost { get; set; } = 0m;
            public long OutMsat { get; set; }
            public decimal? OutCost { get; set; } = 0m;
        }

        private sealed class Disposed
        {
            public List<int> Reliefs { get; } = [];
            public long Msat { get; set; }
            public decimal? Cost { get; set; } = 0m;
            public long ShortfallMsat { get; set; }
            public AccountingLotBucket? ShortfallSource { get; set; }
            public decimal? Proceeds { get; set; }
        }
    }

    private enum SourceKind
    {
        Bucket,
        Imported,
        Acquisition
    }

    /// <summary>A total shared over parts by msat, telescoping (the parts add up to the total exactly).</summary>
    private sealed class Sharer(decimal? total, long msat)
    {
        private long _cumulative;
        private decimal _before;

        public decimal? Next(long part)
        {
            if (total is not { } value)
                return null;

            _cumulative += part;
            var upTo = msat == 0 || _cumulative >= msat
                           ? value
                           : AccountingFiat.RoundStored(value * ((decimal)_cumulative / msat));
            var share = upTo - _before;
            _before = upTo;
            return share;
        }
    }
}