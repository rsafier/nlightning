using System.Globalization;
using System.Text.RegularExpressions;

namespace NLightning.Application.Tests.Accounting.Financial;

/// <summary>
/// A test-side stand-in for <c>hledger check</c> and <c>bean-check</c> (neither runs in CI; NL-602 A3-T6): parses the
/// journals the financial exports write and checks that every transaction balances per commodity once each
/// <c>@@</c> total cost or <c>{{ }}</c> total cost is converted, the way both tools weigh a posting, and that every price
/// directive and account is well formed and declared. It also sums every account in msat and in fiat (the cost or the
/// fiat amount of each posting), so a test can compare the journal with the books.
/// </summary>
/// <remarks>
/// It reads the subset of the two syntaxes the exports write (one posting per line, amounts as <c>N msat</c> /
/// <c>N MSAT</c> with an optional total cost, or <c>F CUR</c>), and reports anything else as an error. A3-T4's golden
/// files go through it too.
/// </remarks>
internal static partial class JournalBalanceChecker
{
    private static readonly CultureInfo s_invariant = CultureInfo.InvariantCulture;

    /// <summary>The result of a check.</summary>
    internal sealed class Result
    {
        public List<string> Errors { get; } = [];
        public int Transactions { get; set; }
        public int Prices { get; set; }

        /// <summary>Per account: the msat posted (quantities) and the fiat (costs and fiat amounts).</summary>
        public Dictionary<string, (long Msat, decimal Fiat)> Accounts { get; } = new(StringComparer.Ordinal);

        public bool IsValid => Errors.Count == 0;

        internal void Add(string account, long msat, decimal fiat)
        {
            var (m, f) = Accounts.GetValueOrDefault(account);
            Accounts[account] = (m + msat, f + fiat);
        }
    }

    private sealed record Posting(string Account, long Msat, decimal Fiat, string? Commodity, bool HasCost,
                                  bool IsTotalCostInBraces);

    /// <summary>Checks an hledger journal.</summary>
    public static Result CheckHledger(string journal, string currency)
    {
        var result = new Result();
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var lines = journal.Split('\n');
        var index = 0;
        while (index < lines.Length)
        {
            var line = lines[index];
            var number = index + 1;
            index++;
            if (line.Length == 0 || line.StartsWith(';'))
                continue;

            if (line.StartsWith("commodity ", StringComparison.Ordinal))
            {
                if (!HledgerCommodity().IsMatch(line))
                    result.Errors.Add($"{number}: bad commodity directive '{line}'");
                continue;
            }

            if (line.StartsWith("account ", StringComparison.Ordinal))
            {
                declared.Add(line["account ".Length..]);
                continue;
            }

            if (line.StartsWith("P ", StringComparison.Ordinal))
            {
                var price = HledgerPrice().Match(line);
                if (!price.Success || price.Groups["cur"].Value != currency
                                   || !Positive(price.Groups["price"].Value))
                    result.Errors.Add($"{number}: bad price directive '{line}'");
                result.Prices++;
                continue;
            }

            if (!DateLine().IsMatch(line))
            {
                result.Errors.Add($"{number}: unexpected line '{line}'");
                continue;
            }

            var postings = new List<Posting>();
            while (index < lines.Length && lines[index].StartsWith("    ", StringComparison.Ordinal))
            {
                var body = lines[index][4..];
                var at = index + 1;
                index++;
                if (body.StartsWith(';'))
                    continue;

                var split = body.IndexOf("  ", StringComparison.Ordinal);
                if (split <= 0)
                {
                    result.Errors.Add($"{at}: posting without an amount '{body}'");
                    continue;
                }

                var account = body[..split];
                if (!declared.Contains(account))
                    result.Errors.Add($"{at}: account '{account}' is not declared");
                if (ParseAmount(StripComment(body[split..]).Trim(), "msat", currency, "@@ ", false) is { } posting)
                    postings.Add(posting with { Account = account });
                else
                    result.Errors.Add($"{at}: bad amount in '{body}'");
            }

            Balance(result, number, postings, currency, "msat");
        }

        return result;
    }

    /// <summary>Checks a beancount ledger.</summary>
    public static Result CheckBeancount(string ledger, string currency)
    {
        var result = new Result();
        var opened = new Dictionary<string, (string Date, bool NoBooking)>(StringComparer.Ordinal);
        var lines = ledger.Split('\n');
        var index = 0;
        while (index < lines.Length)
        {
            var line = lines[index];
            var number = index + 1;
            index++;
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith("option ", StringComparison.Ordinal))
                continue;

            if (BeanCommodity().IsMatch(line))
                continue;

            var open = BeanOpen().Match(line);
            if (open.Success)
            {
                if (!BeanAccount().IsMatch(open.Groups["account"].Value))
                    result.Errors.Add($"{number}: bad account name '{open.Groups["account"].Value}'");
                opened[open.Groups["account"].Value] = (open.Groups["date"].Value, open.Groups["booking"].Success);
                continue;
            }

            var price = BeanPrice().Match(line);
            if (price.Success)
            {
                if (price.Groups["cur"].Value != currency || !Positive(price.Groups["price"].Value))
                    result.Errors.Add($"{number}: bad price directive '{line}'");
                result.Prices++;
                continue;
            }

            var header = BeanTransaction().Match(line);
            if (!header.Success)
            {
                result.Errors.Add($"{number}: unexpected line '{line}'");
                continue;
            }

            var date = header.Groups["date"].Value;
            var postings = new List<Posting>();
            while (index < lines.Length && lines[index].StartsWith("  ", StringComparison.Ordinal))
            {
                var body = lines[index][2..];
                var at = index + 1;
                index++;
                if (body.StartsWith(';') || BeanMetadata().IsMatch(body))
                    continue;

                var split = body.IndexOf("  ", StringComparison.Ordinal);
                if (split <= 0)
                {
                    result.Errors.Add($"{at}: posting without an amount '{body}'");
                    continue;
                }

                var account = body[..split];
                if (!opened.TryGetValue(account, out var state))
                    result.Errors.Add($"{at}: account '{account}' is not opened");
                else if (string.CompareOrdinal(state.Date, date) > 0)
                    result.Errors.Add($"{at}: account '{account}' is used before it is opened");

                var amount = StripComment(body[split..]).Trim();
                var posting = ParseAmount(amount, "MSAT", currency, "@@ ", true);
                if (posting is null)
                {
                    result.Errors.Add($"{at}: bad amount in '{body}'");
                    continue;
                }

                if (posting.IsTotalCostInBraces && !state.NoBooking)
                    result.Errors.Add($"{at}: a cost on '{account}', which is not opened with booking NONE");
                postings.Add(posting with { Account = account });
            }

            Balance(result, number, postings, currency, "MSAT");
        }

        return result;
    }

    // Converted at their costs, a transaction's postings sum to zero in every commodity
    private static void Balance(Result result, int number, List<Posting> postings, string currency, string msat)
    {
        result.Transactions++;
        if (postings.Count < 2)
            result.Errors.Add($"{number}: a transaction with fewer than two postings");

        var weights = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var posting in postings)
        {
            result.Add(posting.Account, posting.Msat, posting.Fiat);
            if (posting.HasCost || posting.Commodity == currency)
                weights[currency] = weights.GetValueOrDefault(currency) + posting.Fiat;
            else
                weights[msat] = weights.GetValueOrDefault(msat) + posting.Msat;
        }

        foreach (var (commodity, sum) in weights)
            if (sum != 0m)
                result.Errors.Add($"{number}: the transaction does not balance: {sum} {commodity}");
    }

    // "N msat", "N msat @@ F CUR" (hledger), "N MSAT {{F CUR}}" (beancount) or "F CUR"
    private static Posting? ParseAmount(string text, string msatCommodity, string currency, string costMarker,
                                        bool braces)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && parts[1] == currency && Decimal(parts[0]) is { } fiat)
            return new Posting(string.Empty, 0, fiat, currency, false, false);

        if (parts.Length < 2 || parts[1] != msatCommodity || !long.TryParse(parts[0], NumberStyles.AllowLeadingSign,
                                                                             s_invariant, out var msat))
            return null;

        if (parts.Length == 2)
            return new Posting(string.Empty, msat, 0m, msatCommodity, false, false);

        var rest = string.Join(' ', parts[2..]);
        if (rest.StartsWith(costMarker, StringComparison.Ordinal))
        {
            var cost = rest[costMarker.Length..].Split(' ');
            if (cost.Length == 2 && cost[1] == currency && Decimal(cost[0]) is { } total && total >= 0m)
                return new Posting(string.Empty, msat, Math.Sign(msat) * total, msatCommodity, true, false);
        }

        if (braces && rest.StartsWith("{{", StringComparison.Ordinal) && rest.EndsWith("}}", StringComparison.Ordinal))
        {
            var cost = rest[2..^2].Split(' ');
            if (cost.Length == 2 && cost[1] == currency && Decimal(cost[0]) is { } total && total >= 0m)
                return new Posting(string.Empty, msat, Math.Sign(msat) * total, msatCommodity, true, true);
        }

        return null;
    }

    private static decimal? Decimal(string text) =>
        decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, s_invariant,
                         out var value)
            ? value
            : null;

    private static bool Positive(string text) => Decimal(text) is > 0m;

    private static string StripComment(string text)
    {
        var comment = text.IndexOf(';');
        return comment < 0 ? text : text[..comment];
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2} ")]
    private static partial Regex DateLine();

    [GeneratedRegex(@"^commodity 1\.(0*)? [A-Za-z]+$")]
    private static partial Regex HledgerCommodity();

    [GeneratedRegex(@"^P \d{4}-\d{2}-\d{2}( \d{2}:\d{2}:\d{2})? msat (?<price>[0-9.]+) (?<cur>[A-Z]{3})$")]
    private static partial Regex HledgerPrice();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2} commodity [A-Z][A-Z0-9'._-]*$")]
    private static partial Regex BeanCommodity();

    [GeneratedRegex(@"^(?<date>\d{4}-\d{2}-\d{2}) open (?<account>\S+)( (?<booking>""NONE""))?$")]
    private static partial Regex BeanOpen();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2} price MSAT (?<price>[0-9.]+) (?<cur>[A-Z]{3})$")]
    private static partial Regex BeanPrice();

    [GeneratedRegex(@"^(?<date>\d{4}-\d{2}-\d{2}) [*!] ""([^""\\]|\\.)*""$")]
    private static partial Regex BeanTransaction();

    [GeneratedRegex(@"^[a-z][a-zA-Z0-9_-]*: ")]
    private static partial Regex BeanMetadata();

    [GeneratedRegex(@"^(Assets|Liabilities|Equity|Income|Expenses)(:[A-Z0-9][A-Za-z0-9-]*)+$")]
    private static partial Regex BeanAccount();
}