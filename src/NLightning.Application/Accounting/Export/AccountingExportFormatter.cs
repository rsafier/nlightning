using System.Globalization;
using System.Text;

namespace NLightning.Application.Accounting.Export;

using Domain.Accounting.Books;
using Domain.Accounting.Books.Export;
using Domain.Accounting.Books.Reports;
using Domain.Accounting.Models;

/// <summary>
/// One entry of an export: the books' entry and, when found, the feed's event it was posted from (for the
/// description).
/// </summary>
internal sealed record AccountingExportItem(AccountingEntry Entry, AccountingEventModel? Event);

/// <summary>
/// Writes the books as text (plan §6.1): an hledger journal, a beancount ledger or CSV. Pure and deterministic: the
/// same entries give the same bytes (golden-file tests), with <c>\n</c> line ends.
/// </summary>
/// <remarks>
/// <para>Amounts are exact msat, never rounded: hledger uses the commodity <c>msat</c> and beancount the currency
/// <c>MSAT</c> (a beancount currency must be upper case).</para>
/// <para>Descriptions come from the event's <c>description</c> detail, which can be another party's text (the
/// description of an invoice we paid): every format strips control characters (no line breaks), hledger replaces the
/// <c>;</c> that would start a comment, beancount escapes its string quoting, and CSV quotes per RFC 4180 and neutralizes
/// a leading <c>= + - @</c> (spreadsheet formula injection).</para>
/// <para>The journal formats leave out entries without postings (memos, failed payments); CSV has one row per posting,
/// so they have none there either.</para>
/// </remarks>
internal abstract class AccountingExportFormatter
{
    protected const string Header = "NLightning accounting export";

    protected static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static AccountingExportFormatter For(AccountingExportFormat format, AccountNames names) => format switch
    {
        AccountingExportFormat.Hledger => new HledgerFormatter(names),
        AccountingExportFormat.Beancount => new BeancountFormatter(names),
        AccountingExportFormat.Csv => new CsvFormatter(names),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown export format.")
    };

    protected AccountingExportFormatter(AccountNames names)
    {
        Names = names;
    }

    protected AccountNames Names { get; }

    /// <summary>Whether the header needs the date of the export's earliest entry and the accounts it uses
    /// (beancount's <c>open</c> directives).</summary>
    public virtual bool NeedsHeaderScan => false;

    /// <summary>
    /// The text that starts the export.
    /// </summary>
    /// <param name="builder">The output.</param>
    /// <param name="firstDate">The earliest entry's time (when <see cref="NeedsHeaderScan"/>; null for an empty
    /// export).</param>
    /// <param name="accounts">The accounts the export posts to (when <see cref="NeedsHeaderScan"/>).</param>
    public abstract void WriteHeader(StringBuilder builder, DateTimeOffset? firstDate,
                                     IReadOnlyCollection<AccountRole> accounts);

    public abstract void WriteEntry(StringBuilder builder, AccountingExportItem item);

    /// <summary>The entry's description: its kind, and the event's <c>description</c> detail when it has one.</summary>
    protected static string Describe(AccountingExportItem item)
    {
        var kind = item.Entry.Kind.ToString();
        var description = item.Event?.Details.GetValueOrDefault("description");
        return string.IsNullOrWhiteSpace(description) ? kind : $"{kind}: {StripControl(description)}";
    }

    protected static string Date(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd", Invariant);

    protected static string Time(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", Invariant);

    protected static string Number(long value) => value.ToString(Invariant);

    /// <summary>Control characters (line breaks and tabs included) become spaces; the text is trimmed.</summary>
    protected static string StripControl(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
            builder.Append(char.IsControl(c) ? ' ' : c);
        return builder.ToString().Trim();
    }

    private sealed class HledgerFormatter(AccountNames names) : AccountingExportFormatter(names)
    {
        private const int AccountWidth = 44;

        public override void WriteHeader(StringBuilder builder, DateTimeOffset? firstDate,
                                         IReadOnlyCollection<AccountRole> accounts)
        {
            builder.Append("; ").Append(Header).Append(" (hledger journal, amounts in millisatoshi)\n");
            builder.Append("commodity 1 msat\n");
            foreach (var role in Enum.GetValues<AccountRole>())
                builder.Append("account ").Append(AccountName(role)).Append('\n');
            builder.Append('\n');
        }

        public override void WriteEntry(StringBuilder builder, AccountingExportItem item)
        {
            var entry = item.Entry;
            if (entry.Postings.Count == 0)
                return;

            // ';' starts a comment in a description
            builder.Append(Date(entry.OccurredAt)).Append(' ').Append(Describe(item).Replace(';', ',')).Append('\n');
            builder.Append("    ; key: ").Append(StripControl(entry.EventKey)).Append('\n');
            builder.Append("    ; seq: ").Append(Number(entry.LedgerSeq)).Append('\n');
            builder.Append("    ; time: ").Append(Time(entry.OccurredAt)).Append('\n');
            foreach (var posting in entry.Postings)
            {
                var account = AccountName(posting.Account);
                builder.Append("    ").Append(account.PadRight(Math.Max(AccountWidth, account.Length + 2)))
                       .Append(Number(posting.AmountMsat).PadLeft(16)).Append(" msat\n");
            }

            builder.Append('\n');
        }

        // hledger ends an account name at two spaces or a tab, and ';' would start a comment
        private string AccountName(AccountRole role)
        {
            var name = StripControl(Names[role]).Replace(';', '_');
            while (name.Contains("  ", StringComparison.Ordinal))
                name = name.Replace("  ", " ", StringComparison.Ordinal);
            return name;
        }
    }

    private sealed class BeancountFormatter(AccountNames names) : AccountingExportFormatter(names)
    {
        private const string Currency = "MSAT";
        private const int AccountWidth = 44;

        public override bool NeedsHeaderScan => true;

        public override void WriteHeader(StringBuilder builder, DateTimeOffset? firstDate,
                                         IReadOnlyCollection<AccountRole> accounts)
        {
            builder.Append("; ").Append(Header).Append(" (beancount, amounts in MSAT = millisatoshi)\n");
            builder.Append("option \"title\" \"NLightning books\"\n");
            builder.Append("option \"operating_currency\" \"").Append(Currency).Append("\"\n");
            builder.Append('\n');
            if (firstDate is not { } date)
                return;

            var opened = new HashSet<string>(StringComparer.Ordinal);
            foreach (var role in accounts.Distinct().OrderBy(r => (int)r))
            {
                var account = AccountName(role);
                if (opened.Add(account))
                    builder.Append(Date(date)).Append(" open ").Append(account).Append(' ').Append(Currency)
                           .Append('\n');
            }

            builder.Append('\n');
        }

        public override void WriteEntry(StringBuilder builder, AccountingExportItem item)
        {
            var entry = item.Entry;
            if (entry.Postings.Count == 0)
                return;

            builder.Append(Date(entry.OccurredAt)).Append(" * ").Append(Quote(Describe(item))).Append('\n');
            builder.Append("  key: ").Append(Quote(StripControl(entry.EventKey))).Append('\n');
            builder.Append("  seq: ").Append(Number(entry.LedgerSeq)).Append('\n');
            builder.Append("  time: ").Append(Quote(Time(entry.OccurredAt))).Append('\n');
            foreach (var posting in entry.Postings)
            {
                var account = AccountName(posting.Account);
                builder.Append("  ").Append(account.PadRight(Math.Max(AccountWidth, account.Length + 2)))
                       .Append(Number(posting.AmountMsat).PadLeft(16)).Append(' ').Append(Currency).Append('\n');
            }

            builder.Append('\n');
        }

        private static string Quote(string text) =>
            "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
          + "\"";

        // A beancount account is Assets|Liabilities|Equity|Income|Expenses followed by components that start with an
        // upper-case letter or a digit and hold letters, digits and dashes: the configured name is mapped onto that,
        // under the root of the account's category (an override never changes the category)
        private string AccountName(AccountRole role)
        {
            var root = AccountingAccountCategories.Of(role) switch
            {
                AccountingAccountCategory.Assets => "Assets",
                AccountingAccountCategory.Liabilities => "Liabilities",
                AccountingAccountCategory.Income => "Income",
                AccountingAccountCategory.Expenses => "Expenses",
                _ => "Equity"
            };

            var components = Names[role].Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                        .ToList();
            if (components.Count > 0 && string.Equals(components[0], root, StringComparison.OrdinalIgnoreCase))
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
                builder.Append(':').Append(role.ToString());

            return builder.ToString();
        }
    }

    private sealed class CsvFormatter(AccountNames names) : AccountingExportFormatter(names)
    {
        public override void WriteHeader(StringBuilder builder, DateTimeOffset? firstDate,
                                         IReadOnlyCollection<AccountRole> accounts) =>
            builder.Append("ledger_seq,occurred_at,event_key,kind,account,amount_msat,description\n");

        public override void WriteEntry(StringBuilder builder, AccountingExportItem item)
        {
            var entry = item.Entry;
            var description = Field(Describe(item));
            foreach (var posting in entry.Postings)
            {
                builder.Append(Number(entry.LedgerSeq)).Append(',')
                       .Append(Time(entry.OccurredAt)).Append(',')
                       .Append(Field(entry.EventKey)).Append(',')
                       .Append(entry.Kind.ToString()).Append(',')
                       .Append(Field(Names[posting.Account])).Append(',')
                       .Append(Number(posting.AmountMsat)).Append(',')
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