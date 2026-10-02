using System.Globalization;

namespace NLightning.Domain.Accounting.Prices;

using Constants;

/// <summary>A line of a price file that could not be read.</summary>
/// <param name="Line">The 1-based line number.</param>
/// <param name="Message">What is wrong with it.</param>
public sealed record AccountingPriceCsvError(int Line, string Message)
{
    public override string ToString() => $"line {Line}: {Message}";
}

/// <summary>A parsed price file: its prices (oldest first) and the lines it could not read.</summary>
/// <param name="Points">The valid prices, oldest first.</param>
/// <param name="Errors">The first <see cref="AccountingPriceCsv.MaxReportedErrors"/> bad lines, in file order.</param>
/// <param name="ErrorCount">How many lines were bad (also those past the reported ones).</param>
public sealed record AccountingPriceCsvResult(
    IReadOnlyList<AccountingPricePoint> Points,
    IReadOnlyList<AccountingPriceCsvError> Errors,
    int ErrorCount)
{
    public bool IsValid => ErrorCount == 0;
}

/// <summary>
/// The operator's price file (D-A11, NL-602 A3-T2): one <c>time,price</c> per line, the time in Unix seconds (or an
/// ISO 8601 time, UTC unless it carries an offset), the price of 1 BTC in the configured currency with a <c>.</c>
/// decimal point. Blank lines and lines starting with <c>#</c> are skipped, and the first line may be a header
/// (<c>unixSeconds,price</c>). The daemon's <c>CsvPriceSource</c> and the client's <c>prices import</c> share this
/// parser, so both report the same errors by line.
/// </summary>
public static class AccountingPriceCsv
{
    /// <summary>The most errors a result lists.</summary>
    public const int MaxReportedErrors = 100;

    /// <summary>The earliest time a price may have (the genesis block's day).</summary>
    public static readonly DateTimeOffset EarliestTime = new(2009, 1, 3, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The largest price accepted: below 10^12 per BTC, so the value of every bitcoin that can exist (21 million) stays
    /// below the 10^20 a <c>decimal(28,8)</c> fiat column holds.
    /// </summary>
    public static readonly decimal MaxPrice = 999_999_999_999.99999999m;

    /// <summary>Parses a whole file.</summary>
    public static AccountingPriceCsvResult Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        using var reader = new StringReader(text);
        return Parse(reader);
    }

    /// <summary>Parses a file line by line; duplicate times are errors (the first one is kept).</summary>
    public static AccountingPriceCsvResult Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var points = new List<AccountingPricePoint>();
        var errors = new List<AccountingPriceCsvError>();
        var errorCount = 0;
        var firstLineOfTime = new Dictionary<DateTimeOffset, int>();
        var lineNumber = 0;
        var sawData = false;

        while (reader.ReadLine() is { } raw)
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            var isFirst = !sawData;
            sawData = true;
            if (isFirst && IsHeader(line))
                continue;

            if (!TryParseLine(line, out var point, out var error))
            {
                AddError(error!);
                continue;
            }

            if (firstLineOfTime.TryGetValue(point!.Time, out var first))
            {
                AddError($"duplicate time {point.Time.ToUnixTimeSeconds()} (first at line {first})");
                continue;
            }

            firstLineOfTime[point.Time] = lineNumber;
            points.Add(point);
        }

        points.Sort((a, b) => a.Time.CompareTo(b.Time));
        return new AccountingPriceCsvResult(points, errors, errorCount);

        void AddError(string message)
        {
            errorCount++;
            if (errors.Count < MaxReportedErrors)
                errors.Add(new AccountingPriceCsvError(lineNumber, message));
        }
    }

    /// <summary>Parses one <c>time,price</c> line (trimmed, not a comment).</summary>
    public static bool TryParseLine(string line, out AccountingPricePoint? point, out string? error)
    {
        ArgumentNullException.ThrowIfNull(line);
        point = null;
        var fields = line.Split(',');
        if (fields.Length != 2)
        {
            error = $"expected 2 fields 'unixSeconds,price', found {fields.Length}";
            return false;
        }

        if (!TryParseTime(fields[0].Trim(), out var time, out error))
            return false;

        if (!TryParsePrice(fields[1].Trim(), out var price, out error))
            return false;

        point = new AccountingPricePoint(time, price);
        return true;
    }

    /// <summary>Checks a price and rounds it to the 8 places the schema stores.</summary>
    public static bool TryValidate(AccountingPricePoint point, out string? error)
    {
        ArgumentNullException.ThrowIfNull(point);
        if (point.Time < EarliestTime || point.Time.UtcDateTime.Year > 9998)
        {
            error = $"time {point.Time:O} is out of range";
            return false;
        }

        if (point.Price <= 0 || point.Price > MaxPrice)
        {
            error = $"price {point.Price.ToString(CultureInfo.InvariantCulture)} must be positive and below 10^12";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryParseTime(string field, out DateTimeOffset time, out string? error)
    {
        time = default;
        if (long.TryParse(field, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seconds))
        {
            if (seconds < EarliestTime.ToUnixTimeSeconds() || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            {
                error = $"time {field} is out of range (Unix seconds from {EarliestTime.ToUnixTimeSeconds()})";
                return false;
            }

            time = DateTimeOffset.FromUnixTimeSeconds(seconds);
            error = null;
            return true;
        }

        if (field.Length > 0 && char.IsAsciiDigit(field[0])
            && DateTimeOffset.TryParse(field, CultureInfo.InvariantCulture,
                                       DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                                       out var parsed))
        {
            time = parsed.ToUniversalTime();
            if (time < EarliestTime)
            {
                error = $"time {field} is before {EarliestTime:yyyy-MM-dd}";
                return false;
            }

            error = null;
            return true;
        }

        error = $"'{Truncate(field)}' is not a time (Unix seconds or ISO 8601)";
        return false;
    }

    private static bool TryParsePrice(string field, out decimal price, out string? error)
    {
        if (!decimal.TryParse(field, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                              CultureInfo.InvariantCulture, out price))
        {
            error = $"'{Truncate(field)}' is not a price (digits with a '.' decimal point)";
            return false;
        }

        if (price <= 0 || price > MaxPrice)
        {
            error = $"price {field} must be positive and below 10^12";
            return false;
        }

        price = Math.Round(price, AccountingSchemaLimits.FiatScale, MidpointRounding.ToEven);
        if (price == 0)
        {
            error = $"price {field} rounds to 0 at {AccountingSchemaLimits.FiatScale} places";
            return false;
        }

        error = null;
        return true;
    }

    private static bool IsHeader(string line)
    {
        var first = line.Split(',')[0].Trim();
        return first.Length > 0 && char.IsAsciiLetter(first[0]) && first.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or ' ');
    }

    private static string Truncate(string field) => field.Length <= 40 ? field : field[..40] + "...";
}