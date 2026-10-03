using System.Globalization;
using System.Text;

namespace NLightning.Application.Accounting.Export.Financial;

using Domain.Accounting.Books;
using Domain.Accounting.Books.Export;
using Domain.Accounting.Books.Reports;
using Domain.Accounting.Constants;
using Domain.Accounting.Financial;
using Domain.Accounting.Financial.Reports;
using Domain.Accounting.Models;

/// <summary>
/// One entry of a financial export: the financial book's entry, the feed's event of its ledger sequence when found (for
/// the description), and the stored prices by id (CSV's price column; may be null or miss some).
/// </summary>
internal sealed record AccountingFinancialExportItem(
    AccountingEntry Entry,
    AccountingEventModel? Event,
    IReadOnlyDictionary<long, AccountingPrice>? Prices = null);

/// <summary>An account a financial export posts to, with its category (beancount's root, the declarations).</summary>
internal sealed record AccountingFinancialExportAccount(string Name, AccountingAccountCategory Category);

/// <summary>
/// What the header of a financial export declares: the date of the earliest entry it writes, the accounts it posts to,
/// the prices its valued lines were valued at (written as <c>P</c> or <c>price</c> directives) and whether an entry needs
/// the fiat rounding account. <see cref="AccountingFinancialExportHeaderBuilder"/> gathers it from the entries.
/// </summary>
internal sealed record AccountingFinancialExportHeader(
    DateTimeOffset? FirstDate,
    IReadOnlyCollection<AccountingFinancialExportAccount> Accounts,
    IReadOnlyList<AccountingPrice> Prices,
    bool UsesRoundingAccount)
{
    public static AccountingFinancialExportHeader Empty { get; } = new(null, [], [], false);
}

/// <summary>
/// How one financial entry is written in a journal (<see cref="AccountingFinancialExportFormatter.Valuate"/>).
/// </summary>
/// <param name="IsValued">Every line with an msat amount has a value in the export's currency with the same sign (or
/// zero), and every fiat-only line is in that currency: the entry is written with its costs.</param>
/// <param name="Residual">The sum of the fiat amounts when valued: 0 for an entry that balances in fiat; otherwise the
/// rounding left by valuing each line on its own, written to the rounding account.</param>
/// <param name="PriceIds">The stored prices of its valued lines.</param>
internal sealed record AccountingFinancialEntryValuation(bool IsValued, decimal Residual, IReadOnlyList<long> PriceIds)
{
    /// <summary>Whether the entry writes anything (a line with an msat amount or a non-zero fiat amount).</summary>
    public bool HasLines { get; init; }
}

/// <summary>
/// Gathers a financial export's header (<see cref="AccountingFinancialExportHeader"/>) from its entries, in any order.
/// </summary>
internal sealed class AccountingFinancialExportHeaderBuilder
{
    private readonly Dictionary<string, AccountingFinancialExportAccount> _accounts = new(StringComparer.Ordinal);
    private readonly string _currency;
    private readonly HashSet<long> _priceIds = [];
    private DateTimeOffset? _firstDate;
    private bool _usesRounding;

    public AccountingFinancialExportHeaderBuilder(string currency)
    {
        _currency = currency;
    }

    /// <summary>The stored prices the entries were valued at (load them, then <see cref="Build"/>).</summary>
    public IReadOnlyCollection<long> PriceIds => _priceIds;

    /// <summary>The earliest time of an entry that writes lines.</summary>
    public DateTimeOffset? FirstDate => _firstDate;

    public void Add(AccountingEntry entry)
    {
        var valuation = AccountingFinancialExportFormatter.Valuate(entry, _currency);
        if (!valuation.HasLines)
            return;

        if (_firstDate is null || entry.OccurredAt < _firstDate)
            _firstDate = entry.OccurredAt;
        foreach (var posting in entry.Postings)
        {
            var name = AccountingFinancialExportFormatter.AccountNameOf(posting);
            _accounts.TryAdd(name, new AccountingFinancialExportAccount(
                                 name, AccountingFiat.CategoryOf(name, posting.Account)));
        }

        foreach (var id in valuation.PriceIds)
            _priceIds.Add(id);
        if (valuation.IsValued && valuation.Residual != 0m)
            _usesRounding = true;
    }

    /// <summary>The header, with the loaded prices (those not referenced are left out; order by time, then id).</summary>
    public AccountingFinancialExportHeader Build(IEnumerable<AccountingPrice> prices) =>
        new(_firstDate,
            _accounts.Values.OrderBy(a => a.Name, StringComparer.Ordinal).ToList(),
            prices.Where(p => _priceIds.Contains(p.Id))
                  .DistinctBy(p => p.Id)
                  .OrderBy(p => p.Time)
                  .ThenBy(p => p.Id)
                  .ToList(),
            _usesRounding);
}

/// <summary>
/// Writes the financial book (NL-602 A3-T6, plan §6.2) as an hledger journal with <c>@@</c> costs and <c>P</c> price
/// directives, a beancount ledger with <c>{{ }}</c> costs on asset lines, <c>@@</c> prices on the others and
/// <c>price</c> directives, or CSV with fiat columns. Pure and deterministic (golden files; <c>\n</c> line ends).
/// </summary>
/// <remarks>
/// <para><b>Amounts.</b> Every line keeps its exact msat (commodity <c>msat</c> / <c>MSAT</c>). A valued line carries its
/// fiat amount as its total cost, so a journal tool balances the entry in fiat; a fiat-only line (no msat, such as a
/// realized gain of A3-T4) is written in the currency alone. Fiat amounts and prices are written exactly
/// (<see cref="AccountingFiat.Format"/>), never rounded; a price directive is the price of one msat (the BTC price over
/// 10^11), exact.</para>
/// <para><b>Entries that are not valued</b> (a line without a value in the export's currency, or a value whose sign
/// differs from its msat) are written in msat only, flagged (<c>; unvalued</c>, beancount's <c>!</c>), with each line's
/// known fiat value and the fiat-only lines in comments: a journal tool still balances them in msat.</para>
/// <para><b>Rounding.</b> A valued entry whose fiat amounts do not sum to zero (each line valued on its own and rounded
/// to 8 places) gets one more fiat-only line to <see cref="RoundingAccount"/> for the residual, so the journal balances;
/// CSV writes the book's rows as they are. A3-T4 contract: a financial entry should balance in fiat (its gain or loss
/// line carries the difference), so this line stays rare.</para>
/// <para><b>Escaping</b> as the operational exports: control characters removed, hledger's <c>;</c> replaced, beancount
/// strings escaped, CSV quoted per RFC 4180 with a leading <c>= + - @</c> neutralized.</para>
/// <para><b>Reuse.</b> <see cref="WriteDocument"/> writes a whole export from a list of entries in one call: A3-T4's golden
/// files of its fixtures go through it (and the test-side journal checker of <c>Application.Tests</c>).</para>
/// </remarks>
internal abstract class AccountingFinancialExportFormatter
{
    /// <summary>The account of the rounding residue of a valued entry (see the remarks).</summary>
    public const string DefaultRoundingAccount = "equity:fiat-rounding";

    protected const string Header = "NLightning accounting export, financial book";
    protected const string LabelDetail = AccountingDetailKeys.Label;
    protected const int AccountWidth = 44;

    protected static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    protected AccountingFinancialExportFormatter(string currency, string roundingAccount)
    {
        Currency = currency;
        RoundingAccount = roundingAccount;
    }

    /// <summary>The export's currency (costs and price directives).</summary>
    public string Currency { get; }

    /// <summary>The account of rounding residues.</summary>
    public string RoundingAccount { get; }

    /// <summary>Whether the header needs <see cref="AccountingFinancialExportHeaderBuilder"/>'s scan of the export.</summary>
    public virtual bool NeedsHeaderScan => true;

    /// <summary>The formatter of a format.</summary>
    /// <param name="format">The format.</param>
    /// <param name="currency">The currency (an ISO 4217 code, see <see cref="AccountingFiat.NormalizeCurrency"/>).</param>
    /// <param name="roundingAccount">The rounding account (null = <see cref="DefaultRoundingAccount"/>).</param>
    public static AccountingFinancialExportFormatter For(AccountingExportFormat format, string currency,
                                                         string? roundingAccount = null)
    {
        var code = AccountingFiat.NormalizeCurrency(currency);
        var rounding = string.IsNullOrWhiteSpace(roundingAccount) ? DefaultRoundingAccount : roundingAccount.Trim();
        return format switch
        {
            AccountingExportFormat.Hledger => new HledgerFormatter(code, rounding),
            AccountingExportFormat.Beancount => new BeancountFormatter(code, rounding),
            AccountingExportFormat.Csv => new CsvFormatter(code, rounding),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown export format.")
        };
    }

    /// <summary>
    /// A whole export in one call (the golden files of A3-T4's fixtures): the header from the entries, then every entry
    /// in (ledger sequence, adjustment) order.
    /// </summary>
    public static string WriteDocument(AccountingExportFormat format, string currency,
                                       IEnumerable<AccountingEntry> entries,
                                       IReadOnlyDictionary<long, AccountingEventModel>? eventsBySeq = null,
                                       IReadOnlyCollection<AccountingPrice>? prices = null,
                                       string? roundingAccount = null)
    {
        var formatter = For(format, currency, roundingAccount);
        var ordered = entries.OrderBy(e => e.LedgerSeq).ThenBy(e => e.Adjustment).ToList();
        var pricesById = (prices ?? []).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        var builder = new StringBuilder();
        var header = new AccountingFinancialExportHeaderBuilder(formatter.Currency);
        foreach (var entry in ordered)
            header.Add(entry);
        formatter.WriteHeader(builder, header.Build(pricesById.Values));
        foreach (var entry in ordered)
            formatter.WriteEntry(builder, new AccountingFinancialExportItem(
                                     entry, eventsBySeq?.GetValueOrDefault(entry.LedgerSeq), pricesById));

        return builder.ToString();
    }

    public abstract void WriteHeader(StringBuilder builder, AccountingFinancialExportHeader header);

    public abstract void WriteEntry(StringBuilder builder, AccountingFinancialExportItem item);

    /// <summary>How an entry is written in a journal in <paramref name="currency"/> (see the class remarks).</summary>
    public static AccountingFinancialEntryValuation Valuate(AccountingEntry entry, string currency)
    {
        var valued = true;
        var hasLines = false;
        var residual = 0m;
        var priceIds = new List<long>();
        foreach (var posting in entry.Postings)
        {
            if (posting.AmountMsat == 0 && (posting.FiatAmount ?? 0m) == 0m)
                continue;

            hasLines = true;
            if (posting.FiatAmount is not { } fiat
             || !string.Equals(posting.FiatCurrency, currency, StringComparison.Ordinal))
            {
                valued = false;
                continue;
            }

            if (posting.AmountMsat != 0 && fiat != 0m && Math.Sign(fiat) != Math.Sign(posting.AmountMsat))
                valued = false;

            residual += fiat;
            if (posting.PriceId is { } priceId && !priceIds.Contains(priceId))
                priceIds.Add(priceId);
        }

        return new AccountingFinancialEntryValuation(valued && hasLines, valued ? residual : 0m, priceIds)
        {
            HasLines = hasLines
        };
    }

    /// <summary>The account name of a financial line (its <see cref="AccountingPosting.AccountName"/>, else the
    /// operational name of its role).</summary>
    public static string AccountNameOf(AccountingPosting posting) =>
        string.IsNullOrWhiteSpace(posting.AccountName) ? AccountNames.Default[posting.Account] : posting.AccountName;

    /// <summary>The entry's description: its kind, the event's label and its <c>description</c> detail when present.</summary>
    protected static string Describe(AccountingFinancialExportItem item)
    {
        var text = new StringBuilder(item.Entry.Kind.ToString());
        if (item.Entry.Adjustment > 0)
            text.Append(" (adjustment ").Append(item.Entry.Adjustment.ToString(Invariant)).Append(')');
        var details = item.Event?.Details;
        var separator = ": ";
        foreach (var key in (string[])[LabelDetail, "description"])
        {
            if (details?.GetValueOrDefault(key) is not { } value || string.IsNullOrWhiteSpace(value))
                continue;

            text.Append(separator).Append(StripControl(value));
            separator = " - ";
        }

        return text.ToString();
    }

    protected static string Date(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd", Invariant);

    protected static string Time(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", Invariant);

    protected static string Number(long value) => value.ToString(Invariant);

    protected static string Fiat(decimal value) => AccountingFiat.Format(value);

    /// <summary>The flags and classification of an entry as one comment text, or null when it has none.</summary>
    protected static string? Annotations(AccountingEntry entry)
    {
        var parts = new List<string>();
        if (entry.Flags != AccountingEntryFlags.None)
            parts.Add("flags " + entry.Flags.ToString().Replace(", ", "|", StringComparison.Ordinal));
        if (entry.Classification is { } classification)
            parts.Add("class " + classification + (entry.RuleId is { } rule ? " " + rule.ToString(Invariant) : ""));
        if (entry.ClosedPeriodId is { } period)
            parts.Add("period " + StripControl(period));
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>Control characters (line breaks and tabs included) become spaces; the text is trimmed.</summary>
    protected static string StripControl(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
            builder.Append(char.IsControl(c) ? ' ' : c);
        return builder.ToString().Trim();
    }

    private sealed class HledgerFormatter(string currency, string roundingAccount)
        : AccountingFinancialExportFormatter(currency, roundingAccount)
    {
        public override void WriteHeader(StringBuilder builder, AccountingFinancialExportHeader header)
        {
            builder.Append("; ").Append(Header).Append(" (hledger journal, amounts in millisatoshi, costs in ")
                   .Append(Currency).Append(")\n");
            builder.Append("commodity 1. msat\n");
            // The currency's display precision is its minor unit; amounts keep every decimal they have
            builder.Append("commodity 1.").Append('0', AccountingFiat.MinorUnits(Currency)).Append(' ').Append(Currency)
                   .Append('\n');
            var names = header.Accounts.Select(a => AccountName(a.Name)).ToList();
            if (header.UsesRoundingAccount)
                names.Add(AccountName(RoundingAccount));
            foreach (var name in names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                builder.Append("account ").Append(name).Append('\n');
            builder.Append('\n');
            if (header.Prices.Count == 0)
                return;

            foreach (var price in header.Prices)
                builder.Append("P ").Append(price.Time.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", Invariant))
                       .Append(" msat ").Append(Fiat(AccountingFiat.PricePerMsat(price.Price))).Append(' ')
                       .Append(price.Currency).Append('\n');
            builder.Append('\n');
        }

        public override void WriteEntry(StringBuilder builder, AccountingFinancialExportItem item)
        {
            var entry = item.Entry;
            var valuation = Valuate(entry, Currency);
            if (!valuation.HasLines)
                return;

            // ';' starts a comment in a description
            builder.Append(Date(entry.OccurredAt)).Append(' ').Append(Describe(item).Replace(';', ',')).Append('\n');
            builder.Append("    ; key: ").Append(StripControl(entry.EventKey)).Append('\n');
            builder.Append("    ; seq: ").Append(Number(entry.LedgerSeq)).Append('\n');
            if (entry.Adjustment > 0)
                builder.Append("    ; adjustment: ").Append(entry.Adjustment.ToString(Invariant)).Append('\n');
            builder.Append("    ; time: ").Append(Time(entry.OccurredAt)).Append('\n');
            if (Annotations(entry) is { } annotations)
                builder.Append("    ; ").Append(annotations).Append('\n');
            if (!valuation.IsValued)
                builder.Append("    ; unvalued: not every line has a value in ").Append(Currency)
                       .Append(", written in msat only\n");

            foreach (var posting in entry.Postings)
            {
                var fiat = posting.FiatAmount;
                if (posting.AmountMsat == 0 && (fiat ?? 0m) == 0m)
                    continue;

                var account = AccountName(AccountNameOf(posting));
                if (posting.AmountMsat == 0)
                {
                    if (valuation.IsValued)
                        Line(builder, account, Fiat(fiat!.Value).PadLeft(16) + " " + Currency);
                    else
                        builder.Append("    ; fiat-only line: ").Append(account).Append(' ').Append(Fiat(fiat!.Value))
                               .Append(' ').Append(posting.FiatCurrency).Append('\n');
                    continue;
                }

                var amount = Number(posting.AmountMsat).PadLeft(16) + " msat";
                if (valuation.IsValued)
                    Line(builder, account, amount + " @@ " + Fiat(Math.Abs(fiat!.Value)) + " " + Currency);
                else
                    Line(builder, account, fiat is { } known
                                               ? amount + "  ; fiat: " + Fiat(known) + " " + posting.FiatCurrency
                                               : amount);
            }

            if (valuation.IsValued && valuation.Residual != 0m)
                Line(builder, AccountName(RoundingAccount),
                     Fiat(-valuation.Residual).PadLeft(16) + " " + Currency + "  ; fiat rounding");

            builder.Append('\n');
        }

        private static void Line(StringBuilder builder, string account, string amount) =>
            builder.Append("    ").Append(account.PadRight(Math.Max(AccountWidth, account.Length + 2))).Append(amount)
                   .Append('\n');

        // hledger ends an account name at two spaces or a tab, and ';' would start a comment
        private static string AccountName(string name)
        {
            var text = StripControl(name).Replace(';', '_');
            while (text.Contains("  ", StringComparison.Ordinal))
                text = text.Replace("  ", " ", StringComparison.Ordinal);
            return text;
        }
    }

    private sealed class BeancountFormatter(string currency, string roundingAccount)
        : AccountingFinancialExportFormatter(currency, roundingAccount)
    {
        private const string Commodity = "MSAT";

        public override void WriteHeader(StringBuilder builder, AccountingFinancialExportHeader header)
        {
            builder.Append("; ").Append(Header).Append(" (beancount, MSAT = millisatoshi, costs in ").Append(Currency)
                   .Append(")\n");
            builder.Append("option \"title\" \"NLightning financial books\"\n");
            builder.Append("option \"operating_currency\" \"").Append(Currency).Append("\"\n");
            builder.Append('\n');
            if (header.FirstDate is { } first)
            {
                var date = Date(first);
                builder.Append(date).Append(" commodity ").Append(Commodity).Append('\n');
                var accounts = header.Accounts.ToList();
                if (header.UsesRoundingAccount)
                    accounts.Add(new AccountingFinancialExportAccount(RoundingAccount, AccountingFiat.CategoryOf(
                                                                          RoundingAccount, AccountRole.Opening)));

                // The lots are ours (A3-T4): beancount books nothing against them ("NONE"), the costs only balance
                var opened = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (name, category) in accounts.Select(a => (Name: AccountName(a.Name, a.Category),
                                                                          a.Category))
                                                        .OrderBy(a => a.Name, StringComparer.Ordinal))
                {
                    if (!opened.Add(name))
                        continue;

                    builder.Append(date).Append(" open ").Append(name);
                    if (category == AccountingAccountCategory.Assets)
                        builder.Append(" \"NONE\"");
                    builder.Append('\n');
                }

                builder.Append('\n');
            }

            if (header.Prices.Count == 0)
                return;

            foreach (var price in header.Prices)
                builder.Append(Date(price.Time)).Append(" price ").Append(Commodity).Append(' ')
                       .Append(Fiat(AccountingFiat.PricePerMsat(price.Price))).Append(' ').Append(price.Currency)
                       .Append('\n');
            builder.Append('\n');
        }

        public override void WriteEntry(StringBuilder builder, AccountingFinancialExportItem item)
        {
            var entry = item.Entry;
            var valuation = Valuate(entry, Currency);
            if (!valuation.HasLines)
                return;

            // '!' marks what needs a look: not valued, or not classified
            var flag = !valuation.IsValued || entry.Flags.HasFlag(AccountingEntryFlags.Unclassified) ? '!' : '*';
            builder.Append(Date(entry.OccurredAt)).Append(' ').Append(flag).Append(' ').Append(Quote(Describe(item)))
                   .Append('\n');
            builder.Append("  key: ").Append(Quote(StripControl(entry.EventKey))).Append('\n');
            builder.Append("  seq: ").Append(Number(entry.LedgerSeq)).Append('\n');
            if (entry.Adjustment > 0)
                builder.Append("  adjustment: ").Append(entry.Adjustment.ToString(Invariant)).Append('\n');
            builder.Append("  time: ").Append(Quote(Time(entry.OccurredAt))).Append('\n');
            if (Annotations(entry) is { } annotations)
                builder.Append("  annotations: ").Append(Quote(annotations)).Append('\n');
            if (!valuation.IsValued)
                builder.Append("  ; unvalued: not every line has a value in ").Append(Currency)
                       .Append(", written in MSAT only\n");

            foreach (var posting in entry.Postings)
            {
                var fiat = posting.FiatAmount;
                if (posting.AmountMsat == 0 && (fiat ?? 0m) == 0m)
                    continue;

                var name = AccountNameOf(posting);
                var category = AccountingFiat.CategoryOf(name, posting.Account);
                var account = AccountName(name, category);
                if (posting.AmountMsat == 0)
                {
                    if (valuation.IsValued)
                        Line(builder, account, Fiat(fiat!.Value).PadLeft(16) + " " + Currency);
                    else
                        builder.Append("  ; fiat-only line: ").Append(account).Append(' ').Append(Fiat(fiat!.Value))
                               .Append(' ').Append(posting.FiatCurrency).Append('\n');
                    continue;
                }

                var amount = Number(posting.AmountMsat).PadLeft(16) + " " + Commodity;
                if (valuation.IsValued)
                {
                    var cost = Fiat(Math.Abs(fiat!.Value)) + " " + Currency;
                    Line(builder, account, category == AccountingAccountCategory.Assets
                                               ? amount + " {{" + cost + "}}"
                                               : amount + " @@ " + cost);
                }
                else
                {
                    Line(builder, account, fiat is { } known
                                               ? amount + " ; fiat: " + Fiat(known) + " " + posting.FiatCurrency
                                               : amount);
                }
            }

            if (valuation.IsValued && valuation.Residual != 0m)
                Line(builder, AccountName(RoundingAccount, AccountingFiat.CategoryOf(RoundingAccount, AccountRole.Opening)),
                     Fiat(-valuation.Residual).PadLeft(16) + " " + Currency + " ; fiat rounding");

            builder.Append('\n');
        }

        private static void Line(StringBuilder builder, string account, string amount) =>
            builder.Append("  ").Append(account.PadRight(Math.Max(AccountWidth, account.Length + 2))).Append(amount)
                   .Append('\n');

        private static string Quote(string text) =>
            "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
          + "\"";

        // A beancount account is Assets|Liabilities|Equity|Income|Expenses followed by components that start with an
        // upper-case letter or a digit and hold letters, digits and dashes: the name is mapped onto that, under the root
        // of its category (as the operational export does)
        private static string AccountName(string name, AccountingAccountCategory category)
        {
            var root = category switch
            {
                AccountingAccountCategory.Assets => "Assets",
                AccountingAccountCategory.Liabilities => "Liabilities",
                AccountingAccountCategory.Income => "Income",
                AccountingAccountCategory.Expenses => "Expenses",
                _ => "Equity"
            };

            var components = name.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                 .ToList();
            if (components.Count > 0 && IsRootWord(components[0])
             && AccountingFiat.CategoryOf(components[0], AccountRole.Opening) == category)
                components.RemoveAt(0);

            var builder = new StringBuilder(root);
            foreach (var component in components)
            {
                var sanitized = new StringBuilder(component.Length + 1);
                foreach (var c in component)
                    sanitized.Append(char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '-');

                if (sanitized.Length == 0)
                    continue;

                if (!char.IsAsciiLetterOrDigit(sanitized[0]))
                    sanitized.Insert(0, 'X');
                sanitized[0] = char.ToUpperInvariant(sanitized[0]);
                builder.Append(':').Append(sanitized);
            }

            if (builder.Length == root.Length)
                builder.Append(":Other");

            return builder.ToString();
        }

        private static bool IsRootWord(string component) =>
            component.ToLowerInvariant() is "assets" or "asset" or "liabilities" or "liability" or "equity" or "income"
                                         or "revenue" or "revenues" or "expenses" or "expense";
    }

    private sealed class CsvFormatter(string currency, string roundingAccount)
        : AccountingFinancialExportFormatter(currency, roundingAccount)
    {
        public override bool NeedsHeaderScan => false;

        public override void WriteHeader(StringBuilder builder, AccountingFinancialExportHeader header) =>
            builder.Append("ledger_seq,adjustment,occurred_at,event_key,kind,account,role,amount_msat,fiat_amount,"
                         + "fiat_currency,price_id,price_per_btc,flags,classification,rule_id,closed_period,"
                         + "description\n");

        public override void WriteEntry(StringBuilder builder, AccountingFinancialExportItem item)
        {
            var entry = item.Entry;
            var description = Field(Describe(item));
            var flags = entry.Flags == AccountingEntryFlags.None
                            ? string.Empty
                            : entry.Flags.ToString().Replace(", ", "|", StringComparison.Ordinal);
            foreach (var posting in entry.Postings)
            {
                var price = posting.PriceId is { } id && item.Prices?.GetValueOrDefault(id) is { } stored
                                ? Fiat(stored.Price)
                                : string.Empty;
                builder.Append(Number(entry.LedgerSeq)).Append(',')
                       .Append(entry.Adjustment.ToString(Invariant)).Append(',')
                       .Append(Time(entry.OccurredAt)).Append(',')
                       .Append(Field(entry.EventKey)).Append(',')
                       .Append(entry.Kind.ToString()).Append(',')
                       .Append(Field(AccountNameOf(posting))).Append(',')
                       .Append(posting.Account.ToString()).Append(',')
                       .Append(Number(posting.AmountMsat)).Append(',')
                       .Append(posting.FiatAmount is { } fiat ? Fiat(fiat) : string.Empty).Append(',')
                       .Append(Field(posting.FiatCurrency ?? string.Empty)).Append(',')
                       .Append(posting.PriceId?.ToString(Invariant) ?? string.Empty).Append(',')
                       .Append(price).Append(',')
                       .Append(flags).Append(',')
                       .Append(entry.Classification?.ToString() ?? string.Empty).Append(',')
                       .Append(entry.RuleId?.ToString(Invariant) ?? string.Empty).Append(',')
                       .Append(Field(entry.ClosedPeriodId ?? string.Empty)).Append(',')
                       .Append(description).Append('\n');
            }
        }

        // RFC 4180 quoting, after neutralizing a leading formula character (a spreadsheet would run it)
        private static string Field(string text)
        {
            var value = StripControl(text);
            if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@')
                value = "'" + value;

            return value.IndexOfAny([',', '"']) >= 0
                       ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
                       : value;
        }
    }
}