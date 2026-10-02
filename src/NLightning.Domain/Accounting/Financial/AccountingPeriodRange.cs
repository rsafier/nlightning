using System.Globalization;

namespace NLightning.Domain.Accounting.Financial;

using Constants;

/// <summary>
/// The name and bounds of an accounting period (A3-T5, <c>nltg accounting close &lt;period&gt;</c>): a calendar month
/// <c>YYYY-MM</c> or a date range <c>YYYY-MM-DD..YYYY-MM-DD</c> whose end date is included. The bounds are UTC: the
/// period holds every time t with <see cref="Start"/> ≤ t &lt; <see cref="End"/>.
/// </summary>
/// <param name="PeriodId">The canonical id (the month, or the range with both dates written out).</param>
/// <param name="Start">The first instant of the period (midnight UTC).</param>
/// <param name="End">The first instant after it (midnight UTC after the last day).</param>
public sealed record AccountingPeriodRange(string PeriodId, DateTimeOffset Start, DateTimeOffset End)
{
    /// <summary>The separator of a date range.</summary>
    public const string RangeSeparator = "..";

    private const string MonthFormat = "yyyy-MM";
    private const string DayFormat = "yyyy-MM-dd";

    /// <summary>Parses a period id; the error says what is expected.</summary>
    public static bool TryParse(string? text, out AccountingPeriodRange? range, out string? error)
    {
        range = null;
        error = null;
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            error = "A period is required: YYYY-MM or YYYY-MM-DD..YYYY-MM-DD.";
            return false;
        }

        var separator = value.IndexOf(RangeSeparator, StringComparison.Ordinal);
        if (separator < 0)
        {
            if (!DateTime.TryParseExact(value, MonthFormat, CultureInfo.InvariantCulture,
                                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                                        out var month))
            {
                error = $"'{value}' is not a period: expected a month YYYY-MM or a range YYYY-MM-DD..YYYY-MM-DD.";
                return false;
            }

            var start = new DateTimeOffset(month.Year, month.Month, 1, 0, 0, 0, TimeSpan.Zero);
            range = new AccountingPeriodRange(start.ToString(MonthFormat, CultureInfo.InvariantCulture), start,
                                              start.AddMonths(1));
            return true;
        }

        if (!TryParseDay(value[..separator], out var first) || !TryParseDay(value[(separator + 2)..], out var last))
        {
            error = $"'{value}' is not a period: a range is YYYY-MM-DD..YYYY-MM-DD (the last day included).";
            return false;
        }

        if (last < first)
        {
            error = $"The period '{value}' ends before it starts.";
            return false;
        }

        var id = first.ToString(DayFormat, CultureInfo.InvariantCulture) + RangeSeparator
               + last.ToString(DayFormat, CultureInfo.InvariantCulture);
        if (id.Length > AccountingSchemaLimits.PeriodIdMaxLength)
        {
            error = $"The period id '{id}' is too long.";
            return false;
        }

        range = new AccountingPeriodRange(id, first, last.AddDays(1));
        return true;
    }

    /// <summary>Parses a period id.</summary>
    /// <exception cref="ArgumentException">Not a period.</exception>
    public static AccountingPeriodRange Parse(string? text) =>
        TryParse(text, out var range, out var error) ? range! : throw new ArgumentException(error, nameof(text));

    private static bool TryParseDay(string text, out DateTimeOffset day)
    {
        day = default;
        if (!DateTime.TryParseExact(text.Trim(), DayFormat, CultureInfo.InvariantCulture,
                                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var value))
            return false;

        day = new DateTimeOffset(value.Year, value.Month, value.Day, 0, 0, 0, TimeSpan.Zero);
        return true;
    }
}