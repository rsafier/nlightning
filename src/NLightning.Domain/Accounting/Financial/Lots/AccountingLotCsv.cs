using System.Globalization;

namespace NLightning.Domain.Accounting.Financial.Lots;

using Constants;
using Prices;

/// <summary>One lot of a lot file or an import (D-A9): when the sats were acquired, how many (msat) and their cost in
/// the book's currency.</summary>
public sealed record AccountingLotPoint(DateTimeOffset Time, long Msat, decimal Cost);

/// <summary>A line of a lot file that could not be read.</summary>
public sealed record AccountingLotCsvError(int Line, string Message)
{
    public override string ToString() => $"line {Line}: {Message}";
}

/// <summary>A parsed lot file: its lots in file order and the lines it could not read.</summary>
public sealed record AccountingLotCsvResult(
    IReadOnlyList<AccountingLotPoint> Lots,
    IReadOnlyList<AccountingLotCsvError> Errors,
    int ErrorCount)
{
    public bool IsValid => ErrorCount == 0;
}

/// <summary>
/// The operator's lot file of <c>nltg accounting lots import &lt;csv&gt;</c> (D-A9, NL-602 A3-T4): one
/// <c>time,sats,cost</c> per line, the acquisition time in Unix seconds or ISO 8601 (as the price file), the amount in
/// sats with up to three decimals (msat), and the total fiat cost of the lot in the book's currency with a <c>.</c>
/// decimal point (0 allowed). Blank lines and lines starting with <c>#</c> are skipped, and the first line may be a
/// header. The client parses the file and the daemon checks every lot again (<see cref="TryValidate"/>).
/// </summary>
public static class AccountingLotCsv
{
    /// <summary>The most errors a result lists.</summary>
    public const int MaxReportedErrors = 100;

    /// <summary>The most lots one import holds.</summary>
    public const int MaxLots = 100_000;

    /// <summary>The largest lot: every bitcoin that can exist (21 million BTC in msat).</summary>
    public const long MaxMsat = 2_100_000_000_000_000_000;

    /// <summary>The largest cost a lot may have (below the 10^20 a <c>decimal(28,8)</c> column holds).</summary>
    public static readonly decimal MaxCost = 99_999_999_999_999_999_999.99999999m;

    /// <summary>Parses a whole file.</summary>
    public static AccountingLotCsvResult Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        using var reader = new StringReader(text);
        var lots = new List<AccountingLotPoint>();
        var errors = new List<AccountingLotCsvError>();
        var errorCount = 0;
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
            if (isFirst && AccountingPriceCsv.IsHeader(line))
                continue;

            if (TryParseLine(line, out var lot, out var error))
            {
                lots.Add(lot!);
                continue;
            }

            errorCount++;
            if (errors.Count < MaxReportedErrors)
                errors.Add(new AccountingLotCsvError(lineNumber, error!));
        }

        if (lots.Count > MaxLots)
        {
            errorCount++;
            errors.Add(new AccountingLotCsvError(lineNumber, $"at most {MaxLots} lots per import ({lots.Count} read)"));
        }

        return new AccountingLotCsvResult(lots, errors, errorCount);
    }

    /// <summary>Parses one <c>time,sats,cost</c> line (trimmed, not a comment).</summary>
    public static bool TryParseLine(string line, out AccountingLotPoint? lot, out string? error)
    {
        ArgumentNullException.ThrowIfNull(line);
        lot = null;
        var fields = line.Split(',');
        if (fields.Length != 3)
        {
            error = $"expected 3 fields 'time,sats,cost', found {fields.Length}";
            return false;
        }

        if (!AccountingPriceCsv.TryParseTime(fields[0].Trim(), out var time, out error))
            return false;

        var satsField = fields[1].Trim();
        if (!decimal.TryParse(satsField, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var sats)
         || sats * 1_000m != decimal.Truncate(sats * 1_000m))
        {
            error = $"'{AccountingPriceCsv.Truncate(satsField)}' is not an amount of sats (at most 3 decimals)";
            return false;
        }

        var costField = fields[2].Trim();
        if (!decimal.TryParse(costField, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var cost))
        {
            error = $"'{AccountingPriceCsv.Truncate(costField)}' is not a cost (digits with a '.' decimal point)";
            return false;
        }

        if (sats * 1_000m > MaxMsat)
        {
            error = $"{satsField} sats is more than 21 million BTC";
            return false;
        }

        var parsed = new AccountingLotPoint(time, (long)(sats * 1_000m),
                                            Math.Round(cost, AccountingSchemaLimits.FiatScale, MidpointRounding.ToEven));
        if (!TryValidate(parsed, out error))
            return false;

        lot = parsed;
        return true;
    }

    /// <summary>Checks one lot: a time from the genesis day, a positive amount, a cost from 0 below 10^20.</summary>
    public static bool TryValidate(AccountingLotPoint lot, out string? error)
    {
        ArgumentNullException.ThrowIfNull(lot);
        if (lot.Time < AccountingPriceCsv.EarliestTime || lot.Time.UtcDateTime.Year > 9998)
        {
            error = $"time {lot.Time:O} is out of range";
            return false;
        }

        if (lot.Msat <= 0 || lot.Msat > MaxMsat)
        {
            error = $"amount {lot.Msat} msat must be positive and at most 21 million BTC";
            return false;
        }

        if (lot.Cost < 0 || lot.Cost > MaxCost)
        {
            error = $"cost {lot.Cost.ToString(CultureInfo.InvariantCulture)} must be 0 or more and below 10^20";
            return false;
        }

        error = null;
        return true;
    }
}