namespace NLightning.Domain.Accounting.Prices;

using Books;
using Financial.Classification;
using Financial.Lots;

/// <summary>
/// The correction of an entry the financial book cannot project again when one of its stored prices is replaced
/// (NL-693; an entry of a closed period, D-A8, or an adjustment of one): the change of value of each line valued with
/// the price, and the line that balances it in fiat. Pure.
/// </summary>
/// <remarks>
/// <para>A line valued with a price (its <see cref="AccountingPosting.PriceId"/>) is at the market value of its msat
/// then: the projector sets the price only on such lines (the asset lines at cost, the realized gains and the cost-basis
/// lines carry none). Its change is <c>FiatValue(msat, new) - FiatValue(msat, old)</c>, so a second replacement of the
/// same price adds the change from the price it replaced, and the corrections add up to the line valued at the last
/// price. A zero-msat line valued with the price (a <c>Price</c> adjustment that carried a late value into the open
/// period) is revalued from the msat of the posting it valued (<see cref="RepricedLine.ValuedMsat"/>).</para>
/// <para><b>The balancing line.</b> When the entry's asset lines were valued at market too (an entry projected without
/// lot costs, or a late valuation), their changes balance the others and only the rounding is left, on
/// <see cref="FinancialAccount.CostBasis"/>. Otherwise the asset lines are at the cost of their lots, which a closed
/// period never changes: the change of what left the node (a disposal: lines with positive msat, the payment, the fee)
/// is a change of the proceeds, so of the realized gain (<see cref="FinancialAccount.RealizedGains"/> when it grows,
/// <see cref="FinancialAccount.RealizedLosses"/> when it shrinks); the change of what came in (negative msat, the
/// income, the opening balances) is a change of the acquisition's value that its lots do not carry, on
/// <see cref="FinancialAccount.CostBasis"/>.</para>
/// </remarks>
public static class AccountingRepricing
{
    /// <summary>
    /// The correction lines of one entry: one zero-msat line per repriced line with its change of value (on its own
    /// account, valued with <paramref name="priceId"/>) and the balancing lines; empty when nothing changes.
    /// </summary>
    /// <param name="lines">The entry's lines valued with the price.</param>
    /// <param name="entryPostings">Every line of the entry (for the asset the balancing lines are booked against).</param>
    /// <param name="priceId">The replaced price's id.</param>
    /// <param name="oldPrice">The price replaced.</param>
    /// <param name="newPrice">The new price.</param>
    /// <param name="currency">The price's currency.</param>
    /// <param name="chart">The financial chart (the balancing accounts' names).</param>
    public static IReadOnlyList<AccountingPosting> Corrections(IReadOnlyList<RepricedLine> lines,
                                                               IReadOnlyList<AccountingPosting> entryPostings,
                                                               long priceId, decimal oldPrice, decimal newPrice,
                                                               string currency, FinancialChart chart)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(entryPostings);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentNullException.ThrowIfNull(chart);

        var corrections = new List<AccountingPosting>();
        decimal outgoing = 0m, incoming = 0m;
        var assetsAtMarket = false;
        foreach (var line in lines)
        {
            var change = AccountingValuation.FiatValue(line.ValuedMsat, newPrice)
                       - AccountingValuation.FiatValue(line.ValuedMsat, oldPrice);
            if (change == 0m)
                continue;

            corrections.Add(new AccountingPosting(line.Line.Account, 0)
            {
                AccountName = line.Line.AccountName,
                FiatAmount = change,
                FiatCurrency = currency,
                PriceId = priceId
            });
            if (FinancialLotRules.IsAsset(line.ValuedRole))
                assetsAtMarket = true;
            if (line.ValuedMsat > 0)
                outgoing += change;
            else
                incoming += change;
        }

        var residual = outgoing + incoming;
        if (corrections.Count == 0 || residual == 0m)
            return corrections;

        var role = FinancialEntryPlanner.PrimaryAssetRole(entryPostings);
        if (assetsAtMarket)
        {
            corrections.Add(FiatLine(role, chart[FinancialAccount.CostBasis], -residual, currency));
            return corrections;
        }

        // The disposals' proceeds changed: the realized gain moves with them; the acquisitions' value changed: the lots
        // keep their cost, the difference stays on the cost-basis line
        if (outgoing != 0m)
            corrections.Add(FiatLine(role, chart[outgoing > 0m
                                                     ? FinancialAccount.RealizedGains
                                                     : FinancialAccount.RealizedLosses], -outgoing, currency));
        if (incoming != 0m)
            corrections.Add(FiatLine(role, chart[FinancialAccount.CostBasis], -incoming, currency));
        return corrections;
    }

    private static AccountingPosting FiatLine(AccountRole role, string accountName, decimal fiat, string currency) =>
        new(role, 0) { AccountName = accountName, FiatAmount = fiat, FiatCurrency = currency };
}

/// <summary>A line valued with a replaced price (NL-693).</summary>
/// <param name="Line">The line as stored.</param>
/// <param name="ValuedMsat">The msat its value is of: its own, or, for a zero-msat late valuation, the msat of the
/// posting it valued.</param>
/// <param name="ValuedRole">The operational role of that posting.</param>
public sealed record RepricedLine(AccountingPosting Line, long ValuedMsat, AccountRole ValuedRole);