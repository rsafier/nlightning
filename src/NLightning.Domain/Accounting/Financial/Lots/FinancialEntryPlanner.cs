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

    /// <summary>The opening balances' lots were imported (D-A9): an opening balance opens no lot.</summary>
    public bool OpeningLotsImported { get; init; }

    /// <summary>
    /// The fact and its reversal are both in the open period (a reorg): neither touches the lots, and the entry is only
    /// valued (its rounding balanced on the cost-basis line), so the pair nets to zero in msat and in fiat.
    /// </summary>
    public bool Cancelled { get; init; }
}

/// <summary>The lot an entry opens (its id comes from the repository).</summary>
public sealed record FinancialLotSpec(
    long Msat,
    decimal? Cost,
    string? Currency,
    long? PriceId,
    AccountingLotOrigin Origin,
    bool BasisEstimated);

/// <summary>
/// The financial entry the planner works out (NL-602 A3-T4): its lines (valued, with the cost-basis adjustment and the
/// realized gain or loss), its flags, the lots it relieves and the lot it opens.
/// </summary>
public sealed record FinancialEntryPlan(
    IReadOnlyList<AccountingPosting> Postings,
    AccountingEntryFlags Flags,
    IReadOnlyList<FinancialLotTake> Reliefs,
    FinancialLotSpec? NewLot)
{
    /// <summary>The msat disposed of that no lot covered (its cost is taken as its proceeds: no gain).</summary>
    public long ShortfallMsat { get; init; }

    /// <summary>The realized gain (negative: a loss) of the entry's disposals, or null when there is none or it is
    /// pending valuation.</summary>
    public decimal? RealizedGain { get; init; }

    /// <summary>A note for the entry (a shortfall), or null.</summary>
    public string? Note { get; init; }

    /// <summary>The entry's fiat sum (0 for a valued entry, by construction).</summary>
    public decimal FiatSum => Postings.Sum(p => p.FiatAmount ?? 0m);
}

/// <summary>
/// The cost-basis rules of the financial book for one entry (NL-602 A3-T4, plan <c>docs/agents/ACCOUNTING_PLAN.md</c>
/// §6.2, D-A9, D-A12). Pure: the lines, the price and the open lots in, the plan out; the pool is not changed.
/// </summary>
/// <remarks>
/// <para><b>Valuation.</b> Every line with an amount gets the market value of its msat at the entry's price
/// (<see cref="AccountingValuation.FiatValue"/>), except a disposal at zero proceeds (a loss, D-A12), which is worth 0.
/// It is all or nothing: without a usable price no line keeps a value (a line valued already, a reversal's copy, keeps
/// its value only when every line has one), and the entry is <see cref="AccountingEntryFlags.Unvalued"/> and
/// <see cref="AccountingEntryFlags.PendingValuation"/>: the projector projects it again once its price is known.</para>
/// <para><b>Lots</b> (D-A12): the disposal lines' msat relieves the pool in the method's order
/// (<see cref="FinancialLotPool.PlanRelief"/>); the acquisition lines' msat opens one lot at their fair value (an
/// opening balance's at the cutover price, <c>BasisEstimated</c>, D-A9, or none when the opening lots were imported);
/// asset and transfer lines change no lot. The disposal's proceeds (the disposal lines' value) are shared among the
/// takes by msat.</para>
/// <para><b>Gain.</b> With every take's cost known, the realized gain is proceeds − cost and gets a fiat-only line
/// (<c>income:gains:realized</c> credited, or <c>expenses:losses:realized</c> debited), and a fiat-only
/// <see cref="FinancialAccount.CostBasis"/> line moves the assets from the market value of their lines to the lots'
/// cost (acquisitions' value − cost relieved): the entry balances in fiat exactly, and over the whole book the assets'
/// fiat is the open lots' cost. With a take whose lot has no cost the gain is pending
/// (<see cref="AccountingEntryFlags.GainPending"/>, never zero) and only the rounding of the market values is balanced
/// on the cost-basis line.</para>
/// </remarks>
public static class FinancialEntryPlanner
{
    /// <summary>Works out the entry (see the class remarks).</summary>
    /// <param name="input">The entry's lines, price and time.</param>
    /// <param name="pool">The open lots (not changed).</param>
    /// <param name="chart">The financial chart (the gain, loss and cost-basis accounts).</param>
    public static FinancialEntryPlan Plan(FinancialEntryPlanInput input, FinancialLotPool pool, FinancialChart chart)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(chart);

        var lines = input.Lines.Where(l => l.AmountMsat != 0).ToList();
        if (lines.Count == 0)
            return new FinancialEntryPlan([], AccountingEntryFlags.None, [], null);

        var currency = pool.Currency;
        var valued = Valuate(lines, input.Price, currency);
        var flags = valued ? AccountingEntryFlags.None : AccountingEntryFlags.Unvalued | AccountingEntryFlags.PendingValuation;

        // Lots: relieve the disposals, then open the acquisitions' lot
        var disposed = 0L;
        var proceeds = 0m;
        var acquired = 0L;
        var acquiredValue = 0m;
        var assetValue = 0m;
        foreach (var line in lines)
        {
            switch (FinancialLotRules.KindOf(line))
            {
                case FinancialLineKind.Disposal:
                    disposed = checked(disposed + line.AmountMsat);
                    proceeds += line.FiatAmount ?? 0m;
                    break;
                case FinancialLineKind.Acquisition:
                    acquired = checked(acquired - line.AmountMsat);
                    acquiredValue -= line.FiatAmount ?? 0m;
                    break;
                case FinancialLineKind.Asset or FinancialLineKind.Transfer:
                    assetValue += line.FiatAmount ?? 0m;
                    break;
            }
        }

        if (input.Cancelled)
        {
            // No lot moves: only the rounding of the market values is balanced
            var residual = valued ? lines.Sum(l => l.FiatAmount ?? 0m) : 0m;
            if (residual != 0m)
                lines.Add(FiatLine(PrimaryAssetRole(lines), chart[FinancialAccount.CostBasis], -residual, currency));
            return new FinancialEntryPlan(lines, flags, [], null);
        }

        // A debit of the opening balances (the reversal of a wallet fact from before the feed, NL-673) corrects the
        // cutover: it relieves the opening (or imported) lots first and fetches their cost, so it realizes nothing
        var openingCorrection = disposed > 0 && lines.All(l => FinancialLotRules.KindOf(l) != FinancialLineKind.Disposal
                                                     || FinancialLotRules.IsOpeningCorrection(l));
        var takes = openingCorrection
                        ? pool.PlanRelief(disposed, input.At, out var shortfall,
                                          l => l.Origin is AccountingLotOrigin.Opening or AccountingLotOrigin.Import)
                        : pool.PlanRelief(disposed, input.At, out shortfall);
        if (valued && disposed > 0)
        {
            if (openingCorrection && takes.All(t => t.Cost is not null))
                (takes, proceeds) = AtCost(lines, takes, shortfall, disposed, proceeds);
            else
                takes = ShareProceeds(takes, shortfall, disposed, proceeds);
        }

        FinancialLotSpec? newLot = null;
        if (acquired > 0 && !(input.IsOpeningBalance && input.OpeningLotsImported))
            newLot = new FinancialLotSpec(acquired, valued ? acquiredValue : null, valued ? currency : null,
                                          valued ? input.Price?.Id : null,
                                          input.IsOpeningBalance ? AccountingLotOrigin.Opening
                                                                 : AccountingLotOrigin.Acquisition,
                                          input.IsOpeningBalance);

        string? note = null;
        var shortfallProceeds = 0m;
        if (shortfall > 0)
        {
            shortfallProceeds = valued ? proceeds - takes.Sum(t => t.Proceeds ?? 0m) : 0m;
            note = $"lot shortfall: {shortfall} msat disposed of without a lot (cost taken as the proceeds)";
        }

        decimal? gain = null;
        if (valued)
        {
            var role = PrimaryAssetRole(lines);
            if (takes.All(t => t.Cost is not null))
            {
                var cost = takes.Sum(t => t.Cost!.Value) + shortfallProceeds;
                var correction = acquiredValue - cost - assetValue;
                if (correction != 0m)
                    lines.Add(FiatLine(role, chart[FinancialAccount.CostBasis], correction, currency));

                if (disposed > 0)
                {
                    gain = proceeds - cost;
                    if (gain > 0m)
                        lines.Add(FiatLine(role, chart[FinancialAccount.RealizedGains], -gain.Value, currency));
                    else if (gain < 0m)
                        lines.Add(FiatLine(role, chart[FinancialAccount.RealizedLosses], -gain.Value, currency));
                }
            }
            else
            {
                // Pending valuation: no gain; only the rounding of the market values is balanced
                flags |= AccountingEntryFlags.GainPending;
                var residual = lines.Sum(l => l.FiatAmount ?? 0m);
                if (residual != 0m)
                    lines.Add(FiatLine(role, chart[FinancialAccount.CostBasis], -residual, currency));
            }
        }
        else if (takes.Count > 0)
        {
            flags |= AccountingEntryFlags.GainPending;
        }

        return new FinancialEntryPlan(lines, flags, takes, newLot)
        {
            ShortfallMsat = shortfall,
            RealizedGain = gain,
            Note = note
        };
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

    private static AccountingPosting FiatLine(AccountRole role, string accountName, decimal fiat, string currency) =>
        new(role, 0) { AccountName = accountName, FiatAmount = fiat, FiatCurrency = currency };

    // Values every line in place; false (and every value removed) when one cannot be valued
    private static bool Valuate(List<AccountingPosting> lines, AccountingPrice? price, string currency)
    {
        var preset = lines.All(l => l.FiatAmount is not null
                                 && string.Equals(l.FiatCurrency, currency, StringComparison.Ordinal));
        if (preset)
            return true;

        if (price is null || !string.Equals(price.Currency, currency, StringComparison.Ordinal))
        {
            for (var i = 0; i < lines.Count; i++)
                lines[i] = lines[i] with { FiatAmount = null, FiatCurrency = null, PriceId = null };
            return false;
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var zero = FinancialLotRules.KindOf(line) == FinancialLineKind.Disposal
                    && FinancialLotRules.HasZeroProceeds(line.Account);
            lines[i] = line with
            {
                FiatAmount = zero ? 0m : AccountingValuation.FiatValue(line.AmountMsat, price.Price),
                FiatCurrency = currency,
                PriceId = price.Id
            };
        }

        return true;
    }

    // A correction's proceeds are the cost of what it relieves (the shortfall keeps its market share): the disposal
    // lines are revalued to that total, shared by msat (telescoping), and each take fetches its own cost
    private static (IReadOnlyList<FinancialLotTake> Takes, decimal Proceeds) AtCost(
        List<AccountingPosting> lines, IReadOnlyList<FinancialLotTake> takes, long shortfall, long disposed,
        decimal marketProceeds)
    {
        var cost = takes.Sum(t => t.Cost!.Value);
        var shortfallShare = shortfall == 0
                                 ? 0m
                                 : marketProceeds - AccountingFiat.RoundStored(
                                       marketProceeds * ((decimal)(disposed - shortfall) / disposed));
        var total = cost + shortfallShare;
        var cumulative = 0L;
        var before = 0m;
        for (var i = 0; i < lines.Count; i++)
        {
            if (FinancialLotRules.KindOf(lines[i]) != FinancialLineKind.Disposal)
                continue;

            cumulative += lines[i].AmountMsat;
            var upTo = cumulative == disposed
                           ? total
                           : AccountingFiat.RoundStored(total * ((decimal)cumulative / disposed));
            lines[i] = lines[i] with { FiatAmount = upTo - before };
            before = upTo;
        }

        return (takes.Select(t => t with { Proceeds = t.Cost }).ToList(), total);
    }

    // The proceeds shared by msat (telescoping, so the shares add up to the proceeds; the shortfall keeps the rest)
    private static IReadOnlyList<FinancialLotTake> ShareProceeds(IReadOnlyList<FinancialLotTake> takes, long shortfall,
                                                                 long disposed, decimal proceeds)
    {
        var shared = new List<FinancialLotTake>(takes.Count);
        var cumulative = 0L;
        var before = 0m;
        foreach (var take in takes)
        {
            cumulative += take.Msat;
            var upTo = cumulative == disposed && shortfall == 0
                           ? proceeds
                           : AccountingFiat.RoundStored(proceeds * ((decimal)cumulative / disposed));
            shared.Add(take with { Proceeds = upTo - before });
            before = upTo;
        }

        return shared;
    }
}