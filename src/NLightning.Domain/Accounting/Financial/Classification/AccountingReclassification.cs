namespace NLightning.Domain.Accounting.Financial.Classification;

using Books;

/// <summary>
/// The reclassification of a closed period's fact (NL-660, plan <c>docs/agents/ACCOUNTING_PLAN.md</c> D-A8): a closed
/// entry is never rewritten, so a change of its classification (<c>classify set|unset</c>, a rule added, removed,
/// enabled or disabled) is posted as an adjustment of the open period that moves the fact's classifiable lines from the
/// account the book holds them in now to the new one, at their original values (msat and fiat). Pure.
/// </summary>
/// <remarks>
/// <para><b>The fact's lines.</b> The base entry (the fact's adjustment-0 entry, or its
/// <see cref="AccountingEntryFlags.LateFact"/> adjustment when it reached the book late) and the
/// <see cref="AccountingAdjustmentReason.Price"/> adjustments that valued its lines after the close are the lines the
/// classification names; the earlier reclassifications (<see cref="DedupePrefix"/> adjustments) say where they are
/// now. The move is the difference, per line role, account, currency and price, between the lines renamed by the new
/// classification and the lines as they are now, so it is empty when nothing changes and a reclassification back
/// undoes the previous one exactly.</para>
/// <para>Only the classifiable roles (<see cref="FinancialChart.IsClassifiable"/>) move: the asset, fee, cost-basis and
/// gain lines keep their accounts, and no lot moves (a line's lot kind follows its role, NL-674).</para>
/// </remarks>
public static class AccountingReclassification
{
    /// <summary>The dedupe key prefix of a reclassification adjustment (<c>reclass:{n}</c>).</summary>
    public const string DedupePrefix = "reclass:";

    private const string ReclassTag = "[" + DedupePrefix;
    private const string PriceTag = "[price:";

    /// <summary>The fact's base entry among the financial entries of its event key, or null.</summary>
    public static AccountingEntry? BaseEntry(IReadOnlyList<AccountingEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries.FirstOrDefault(e => e.Adjustment == 0)
            ?? entries.Where(e => e.Flags.HasFlag(AccountingEntryFlags.LateFact)).MinBy(e => e.Adjustment);
    }

    /// <summary>Whether <paramref name="entry"/> is a reclassification adjustment.</summary>
    public static bool IsReclassification(AccountingEntry entry) =>
        entry.Adjustment > 0 && entry.Note?.StartsWith(ReclassTag, StringComparison.Ordinal) == true;

    /// <summary>The dedupe key of the next reclassification of a fact with these entries.</summary>
    public static string NextDedupeKey(IReadOnlyList<AccountingEntry> entries) =>
        DedupePrefix + (entries.Count(IsReclassification) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The lines that move the fact's classifiable lines to the accounts <paramref name="accountNameOf"/> gives their
    /// roles (balanced in msat and in fiat), or an empty list when they are there already or the fact has no base entry.
    /// </summary>
    /// <param name="entries">Every financial entry of the fact's event key.</param>
    /// <param name="accountNameOf">The account of a classifiable line of a role under the new classification.</param>
    /// <param name="unvaluedCurrency">When set, a move of a line left unvalued (a forced close) carries 0 in this
    /// currency instead of no value (NL-681): it is never valued later, since the line's value, once found, is posted
    /// by its price adjustment on the account the line is in then (<see cref="CurrentAccountOf"/>).</param>
    public static IReadOnlyList<AccountingPosting> PlanMove(IReadOnlyList<AccountingEntry> entries,
                                                            Func<AccountRole, string> accountNameOf,
                                                            string? unvaluedCurrency = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(accountNameOf);
        if (BaseEntry(entries) is not { } baseEntry)
            return [];

        var baseLines = FactLines(entries, baseEntry).ToList();
        var sums = new Dictionary<LineKey, (long Msat, decimal Fiat)>();
        var order = new List<LineKey>();

        // Where the lines are now (subtracted) and where the new classification puts them (added)
        foreach (var line in baseLines.Concat(entries.Where(IsReclassification).SelectMany(Classifiable)))
            Add(sums, order, line, line.AccountName ?? string.Empty, -1);
        var targets = new HashSet<LineKey>();
        foreach (var line in baseLines)
            targets.Add(Add(sums, order, line, accountNameOf(line.Account), 1));

        // The lines out of the old accounts first, then the lines into the new ones
        var moves = new List<AccountingPosting>();
        foreach (var key in order.Where(k => !targets.Contains(k)).Concat(order.Where(targets.Contains)))
        {
            var (msat, fiat) = sums[key];
            if (msat == 0 && fiat == 0m)
                continue;

            moves.Add(new AccountingPosting(key.Role, msat)
            {
                AccountName = key.Name,
                FiatAmount = key.Currency is null ? unvaluedCurrency is null ? null : 0m : fiat,
                FiatCurrency = key.Currency ?? unvaluedCurrency,
                PriceId = key.PriceId
            });
        }

        return moves;
    }

    /// <summary>
    /// The account the reclassifications hold a line of the fact in now (NL-681): the account of
    /// <paramref name="line"/>'s role whose msat, over the line itself and the reclassification adjustments, has the
    /// line's sign and covers it; the line's own account when none does (or when it never moved).
    /// </summary>
    /// <param name="entries">Every financial entry of the fact's event key.</param>
    /// <param name="line">A classifiable line of the fact's base entry.</param>
    public static string? CurrentAccountOf(IReadOnlyList<AccountingEntry> entries, AccountingPosting line)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(line);
        var moves = entries.Where(IsReclassification).SelectMany(Classifiable).Where(p => p.Account == line.Account)
                           .ToList();
        if (moves.Count == 0 || line.AmountMsat == 0)
            return line.AccountName;

        var held = new Dictionary<string, long>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var posting in moves.Prepend(line))
        {
            var name = posting.AccountName ?? string.Empty;
            if (held.TryAdd(name, 0))
                order.Add(name);
            held[name] = checked(held[name] + posting.AmountMsat);
        }

        var found = order.Where(n => Math.Sign(held[n]) == Math.Sign(line.AmountMsat)
                                  && Math.Abs(held[n]) >= Math.Abs(line.AmountMsat))
                         .ToList();
        return found.Count == 1 ? found[0] : line.AccountName;
    }

    /// <summary>
    /// Whether the fact of <paramref name="entries"/> still sits in an unclassified account of
    /// <paramref name="chart"/>: true for an entry never reclassified (its flag decides), else whether a classifiable line
    /// with msat is left in one after the reclassifications (NL-667).
    /// </summary>
    public static bool IsStillUnclassified(IReadOnlyList<AccountingEntry> entries, FinancialChart chart)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(chart);
        if (!entries.Any(IsReclassification) || BaseEntry(entries) is not { } baseEntry)
            return true;

        return FactLines(entries, baseEntry).Concat(entries.Where(IsReclassification).SelectMany(Classifiable))
                                            .GroupBy(l => l.AccountName ?? string.Empty, StringComparer.Ordinal)
                                            .Any(g => g.Sum(l => l.AmountMsat) != 0 && chart.IsUnclassified(g.Key));
    }

    // The base entry's classifiable lines and those of the price adjustments that valued them
    private static IEnumerable<AccountingPosting> FactLines(IReadOnlyList<AccountingEntry> entries,
                                                            AccountingEntry baseEntry) =>
        Classifiable(baseEntry).Concat(entries.Where(e => e.Adjustment > 0
                                                       && e.Note?.StartsWith(PriceTag, StringComparison.Ordinal) == true)
                                              .SelectMany(Classifiable));

    private static IEnumerable<AccountingPosting> Classifiable(AccountingEntry entry) =>
        entry.Postings.Where(p => FinancialChart.IsClassifiable(p.Account));

    private static LineKey Add(Dictionary<LineKey, (long Msat, decimal Fiat)> sums, List<LineKey> order,
                               AccountingPosting line, string name, int sign)
    {
        var key = new LineKey(line.Account, name, line.FiatAmount is null ? null : line.FiatCurrency,
                              line.FiatAmount is null ? null : line.PriceId);
        if (!sums.TryGetValue(key, out var sum))
            order.Add(key);

        sums[key] = (checked(sum.Msat + (sign * line.AmountMsat)), sum.Fiat + (sign * (line.FiatAmount ?? 0m)));
        return key;
    }

    private readonly record struct LineKey(AccountRole Role, string Name, string? Currency, long? PriceId);
}