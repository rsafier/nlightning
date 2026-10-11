using System.Globalization;

namespace NLightning.Client.Printers;

using Transport.Ipc.Responses;

/// <summary>
/// Prints a report of the financial book or the risk-weighted capital (<c>accounting report ... --book financial</c>,
/// NL-602 A3-T6). Amounts are msat with their exact satoshis; fiat amounts are rounded to the currency's minor unit
/// here, for display only (the response carries them exact).
/// </summary>
public sealed class AccountingFinancialReportPrinter
{
    private const int NameWidth = 40;

    private static readonly CultureInfo s_inv = CultureInfo.InvariantCulture;

    private readonly TextWriter _output;

    public AccountingFinancialReportPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(AccountingReportIpcResponse item, AccountingFinancialReportIpcResponse report)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(report);
        if (report.BalanceSheet is { } sheet)
            PrintBalanceSheet(item, report, sheet);
        else if (report.IncomeStatement is { } statement)
            PrintIncomeStatement(item, report, statement);
        else if (report.RealizedGains is { } gains)
            PrintGains(item, report, gains);
        else if (report.Lots is { } lots)
            PrintLots(item, report, lots);
        else if (report.Register is { } register)
            PrintRegister(item, report, register);
        else if (report.Unvalued is { } unvalued)
            PrintUnvalued(item, unvalued);
        else if (report.RiskCapital is { } risk)
            PrintRisk(report, risk);
        else
            _output.WriteLine(string.Format(s_inv, "{0}: nothing to report", item.KindName));
    }

    /// <summary>A fiat amount rounded to <paramref name="minorUnits"/> decimals (away from zero), with its code.</summary>
    internal static string Fiat(string? amount, string currency, int minorUnits)
    {
        if (amount is null || !decimal.TryParse(amount, NumberStyles.Number, s_inv, out var value))
            return "-";

        return Math.Round(value, minorUnits, MidpointRounding.AwayFromZero).ToString("F" + minorUnits, s_inv) + " "
             + currency;
    }

    private string Fiat(AccountingFinancialReportIpcResponse report, string? amount) =>
        Fiat(amount, report.Currency, report.MinorUnits);

    private static string Amount(long msat) => AccountingReportPrinter.Amount(msat);

    private static string Time(long? unixMilliseconds) => AccountingReportPrinter.Time(unixMilliseconds);

    private static string Period(AccountingReportIpcResponse item) =>
        item.SinceUnixMilliseconds is null && item.UntilUnixMilliseconds is null
            ? "(all time)"
            : string.Format(s_inv, "from {0} to {1}", Time(item.SinceUnixMilliseconds),
                            item.UntilUnixMilliseconds is null ? "now" : Time(item.UntilUnixMilliseconds));

    private string PriceText(AccountingFinancialReportIpcResponse report, AccountingReportPriceIpcResponse price) =>
        string.Format(s_inv, "{0} per BTC ({1})", Fiat(report, price.PricePerBitcoin),
                      price.PriceId is null ? "given" : $"stored price #{price.PriceId} of {Time(price.TimeUnixMilliseconds)}");

    private void PrintBalanceSheet(AccountingReportIpcResponse item, AccountingFinancialReportIpcResponse report,
                                   AccountingFinancialBalanceSheetIpcResponse sheet)
    {
        _output.WriteLine(string.Format(s_inv, "Financial balance sheet at {0} in {1} (financial book through #{2})",
                                        item.UntilUnixMilliseconds is null ? "now" : Time(item.UntilUnixMilliseconds),
                                        report.Currency, item.ProjectedLedgerSeq));
        Section(report, "Assets", sheet.Assets, sheet.TotalAssetsMsat, sheet.TotalAssetsFiat);
        Section(report, "Liabilities", sheet.Liabilities, sheet.TotalLiabilitiesMsat, sheet.TotalLiabilitiesFiat);
        Section(report, "Equity", sheet.Equity, sheet.TotalEquityMsat, sheet.TotalEquityFiat);
        _output.WriteLine(string.Format(s_inv, "  {0}{1}  {2}", "Retained earnings (net income)".PadRight(NameWidth + 2),
                                        Amount(sheet.RetainedEarningsMsat), Fiat(report, sheet.RetainedEarningsFiat)));
        if (sheet.Price is { } price)
            _output.WriteLine(string.Format(s_inv, "  Assets at {0}: {1}", PriceText(report, price),
                                            Fiat(report, sheet.TotalAssetsMarketValue)));
        _output.WriteLine(string.Format(s_inv, "  Assets = liabilities + equity + earnings: {0} in msat, {1} in {2}",
                                        sheet.IsBalanced ? "yes" : "NO (the books are inconsistent)",
                                        sheet.IsFiatBalanced ? "yes" : "no", report.Currency));
        if (sheet.UnvaluedPostings > 0)
            _output.WriteLine(string.Format(s_inv, "  {0} posting(s) have no {1} value yet: the fiat amounts are partial "
                                                 + "(accounting report unvalued)", sheet.UnvaluedPostings,
                                            report.Currency));
    }

    private void PrintIncomeStatement(AccountingReportIpcResponse item, AccountingFinancialReportIpcResponse report,
                                      AccountingFinancialIncomeStatementIpcResponse statement)
    {
        _output.WriteLine(string.Format(s_inv, "Financial income statement {0} in {1} (financial book through #{2})",
                                        Period(item), report.Currency, item.ProjectedLedgerSeq));
        Section(report, "Income", statement.Income, statement.TotalIncomeMsat, statement.TotalIncomeFiat);
        Section(report, "Expenses", statement.Expenses, statement.TotalExpensesMsat, statement.TotalExpensesFiat);
        _output.WriteLine(string.Format(s_inv, "  {0}{1}  {2}", "Net income".PadRight(NameWidth + 2),
                                        Amount(statement.NetIncomeMsat), Fiat(report, statement.NetIncomeFiat)));
        if (statement.UnvaluedPostings > 0)
            _output.WriteLine(string.Format(s_inv, "  {0} posting(s) have no {1} value yet: the fiat amounts are partial",
                                            statement.UnvaluedPostings, report.Currency));
    }

    private void PrintGains(AccountingReportIpcResponse item, AccountingFinancialReportIpcResponse report,
                            AccountingRealizedGainsIpcResponse gains)
    {
        _output.WriteLine(string.Format(s_inv, "Realized gains {0} by {1} in {2}", Period(item),
                                        gains.GroupingName.ToLowerInvariant(), report.Currency));
        foreach (var line in gains.Periods.Append(gains.Total))
        {
            _output.WriteLine(string.Format(s_inv,
                                            "  {0,-10} disposed {1} in {2} relief(s): proceeds {3}, cost {4}, gain {5} "
                                          + "(short {6}, long {7})", line.Period, Amount(line.DisposedMsat),
                                            line.Reliefs, Fiat(report, line.Proceeds), Fiat(report, line.CostBasis),
                                            Fiat(report, line.Gain), Fiat(report, line.ShortTermGain),
                                            Fiat(report, line.LongTermGain)));
            if (line.PendingValuation > 0)
                _output.WriteLine(string.Format(s_inv, "  {0,-10} pending valuation: {1} relief(s), {2}", string.Empty,
                                                line.PendingValuation, Amount(line.PendingValuationMsat)));
            if (line.BasisEstimatedMsat > 0)
                _output.WriteLine(string.Format(s_inv, "  {0,-10} from lots with an estimated basis: {1}", string.Empty,
                                                Amount(line.BasisEstimatedMsat)));
        }
    }

    private void PrintLots(AccountingReportIpcResponse item, AccountingFinancialReportIpcResponse report,
                           AccountingLotsIpcResponse lots)
    {
        _output.WriteLine(string.Format(s_inv, "{0} in {1}: {2} open lot(s), {3}", item.KindName, report.Currency,
                                        lots.OpenLots, Amount(lots.RemainingMsat)));
        _output.WriteLine(string.Format(s_inv, "  Cost basis {0}{1}", Fiat(report, lots.CostBasis),
                                        lots.UnvaluedLots > 0
                                            ? string.Format(s_inv, " ({0} lot(s), {1} without a cost yet)",
                                                            lots.UnvaluedLots, Amount(lots.UnvaluedMsat))
                                            : string.Empty));
        if (lots.Price is { } price)
            _output.WriteLine(string.Format(s_inv, "  At {0}: market value {1}, unrealized gain {2}",
                                            PriceText(report, price), Fiat(report, lots.MarketValue),
                                            Fiat(report, lots.UnrealizedGain)));
        if (lots.BasisEstimatedMsat > 0)
            _output.WriteLine(string.Format(s_inv, "  Estimated basis (opening lots): {0}",
                                            Amount(lots.BasisEstimatedMsat)));
        foreach (var lot in lots.Lots)
        {
            // The bucket and, for a part moved from another lot, when its sats were acquired (NL-657)
            var held = (lot.Account is { } bucket ? " in " + bucket : string.Empty)
                     + (lot.HeldSinceUnixMilliseconds is { } since
                            ? " (held since " + Time(since) + ")"
                            : string.Empty);
            _output.WriteLine(string.Format(s_inv, "  #{0} {1} {2}{3}{8}: {4} of {5}, cost {6}{7}", lot.Id,
                                            Time(lot.AcquiredAtUnixMilliseconds), lot.Origin,
                                            lot.BasisEstimated ? " (estimated)" : string.Empty,
                                            Amount(lot.RemainingMsat), Amount(lot.OriginalMsat),
                                            Fiat(report, lot.RemainingCostBasis),
                                            lot.MarketValue is null
                                                ? string.Empty
                                                : string.Format(s_inv, ", value {0}, gain {1}",
                                                                Fiat(report, lot.MarketValue),
                                                                Fiat(report, lot.UnrealizedGain)), held));
        }

        if (lots.HasMore)
            _output.WriteLine(string.Format(s_inv, "More: --after {0}", lots.NextAfterLotId));
    }

    private void PrintRegister(AccountingReportIpcResponse item, AccountingFinancialReportIpcResponse report,
                               AccountingFinancialRegisterIpcResponse register)
    {
        _output.WriteLine(string.Format(s_inv, "{0} of the financial book: {1} entr{2} (financial book through #{3})",
                                        item.KindName, register.Entries.Count,
                                        register.Entries.Count == 1 ? "y" : "ies", item.ProjectedLedgerSeq));
        foreach (var entry in register.Entries)
        {
            _output.WriteLine(string.Format(s_inv, "#{0}{1} {2} {3} {4}{5}", entry.LedgerSeq,
                                            entry.Adjustment > 0 ? ":" + entry.Adjustment : string.Empty,
                                            Time(entry.OccurredAtUnixMilliseconds), entry.KindName, entry.EventKey,
                                            entry.Flags == 0 ? string.Empty : " [" + entry.FlagNames + "]"));
            foreach (var posting in entry.Postings)
                _output.WriteLine(string.Format(s_inv, "    {0}{1}  {2}", posting.Name.PadRight(NameWidth),
                                                Amount(posting.AmountMsat),
                                                posting.FiatAmount is null
                                                    ? "unvalued"
                                                    : Fiat(posting.FiatAmount, posting.FiatCurrency ?? report.Currency,
                                                           report.MinorUnits)));
        }

        if (register.HasMore)
            _output.WriteLine(string.Format(s_inv, "More: --after {0}:{1}", register.NextAfter,
                                            register.NextAfterAdjustment));
    }

    private void PrintUnvalued(AccountingReportIpcResponse item, AccountingUnvaluedIpcResponse unvalued)
    {
        _output.WriteLine(string.Format(s_inv, "Unvalued postings of the financial book: {0}{1} (financial book through "
                                             + "#{2})", unvalued.Postings.Count, unvalued.HasMore ? "+" : string.Empty,
                                        item.ProjectedLedgerSeq));
        foreach (var posting in unvalued.Postings)
            _output.WriteLine(string.Format(s_inv, "  #{0}{1}/{2} {3} {4}{5}{6}", posting.LedgerSeq,
                                            posting.Adjustment > 0 ? ":" + posting.Adjustment : string.Empty,
                                            posting.Index, Time(posting.OccurredAtUnixMilliseconds),
                                            posting.Name.PadRight(NameWidth), Amount(posting.AmountMsat),
                                            posting.ClosedPeriodId is { } period ? " (closed " + period + ")" : ""));
    }

    private void PrintRisk(AccountingFinancialReportIpcResponse report, AccountingRiskCapitalIpcResponse risk)
    {
        _output.WriteLine(string.Format(s_inv, "Risk-weighted capital at {0} (block {1})",
                                        Time(risk.TakenAtUnixMilliseconds), risk.BlockHeight));
        foreach (var line in risk.Lines)
            _output.WriteLine(string.Format(s_inv, "  {0}{1} x {2} = {3}", line.State.PadRight(28),
                                            Amount(line.AmountMsat), line.Weight, Amount(line.WeightedMsat)));
        _output.WriteLine(string.Format(s_inv, "  {0}{1}", "Gross (weight above 0)".PadRight(28), Amount(risk.GrossMsat)));
        _output.WriteLine(string.Format(s_inv, "  {0}{1}{2}", "Weighted".PadRight(28), Amount(risk.WeightedMsat),
                                        risk.Price is { } price
                                            ? string.Format(s_inv, " = {0} at {1}", Fiat(report, risk.WeightedFiat),
                                                            PriceText(report, price))
                                            : string.Empty));
        _output.WriteLine(string.Format(s_inv, "  {0}{1}", "Peers' balances (excluded)".PadRight(28),
                                        Amount(risk.ExcludedRemoteMsat)));
    }

    private void Section(AccountingFinancialReportIpcResponse report, string title,
                         List<AccountingFinancialAccountIpcResponse> lines, long totalMsat, string totalFiat)
    {
        _output.WriteLine(string.Format(s_inv, "  {0}", title));
        foreach (var line in lines)
            _output.WriteLine(string.Format(s_inv, "    {0}{1}  {2}{3}{4}", line.Name.PadRight(NameWidth),
                                            Amount(line.AmountMsat), Fiat(report, line.FiatAmount),
                                            line.UnvaluedPostings > 0
                                                ? string.Format(s_inv, " ({0} unvalued)", line.UnvaluedPostings)
                                                : string.Empty,
                                            line.MarketValue is null
                                                ? string.Empty
                                                : ", market " + Fiat(report, line.MarketValue)));
        _output.WriteLine(string.Format(s_inv, "    {0}{1}  {2}", "Total".PadRight(NameWidth), Amount(totalMsat),
                                        Fiat(report, totalFiat)));
    }
}