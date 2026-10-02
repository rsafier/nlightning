using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Accounting.Books;
using Domain.Accounting.Financial.Reports;
using Domain.Client.Responses;

/// <summary>
/// A report of the financial book in the response to AccountingReport (ClientCommand 43, NL-602 A3-T6): the field of
/// the kind asked for is set. Fiat amounts and prices are exact decimal strings (invariant culture, never rounded: the
/// client rounds them to <see cref="MinorUnits"/> for display). Keys are append-only.
/// </summary>
[MessagePackObject]
public sealed class AccountingFinancialReportIpcResponse
{
    /// <summary>The ISO 4217 code of the fiat amounts.</summary>
    [Key(0)] public required string Currency { get; init; }

    /// <summary>The decimals the currency shows (ISO 4217 minor unit).</summary>
    [Key(1)] public required int MinorUnits { get; init; }

    [Key(2)] public AccountingFinancialBalanceSheetIpcResponse? BalanceSheet { get; init; }
    [Key(3)] public AccountingFinancialIncomeStatementIpcResponse? IncomeStatement { get; init; }
    [Key(4)] public AccountingRealizedGainsIpcResponse? RealizedGains { get; init; }

    /// <summary>The open lots (kinds lots and unrealized gains).</summary>
    [Key(5)] public AccountingLotsIpcResponse? Lots { get; init; }

    /// <summary>The financial register (kinds register and unclassified).</summary>
    [Key(6)] public AccountingFinancialRegisterIpcResponse? Register { get; init; }

    [Key(7)] public AccountingUnvaluedIpcResponse? Unvalued { get; init; }
    [Key(8)] public AccountingRiskCapitalIpcResponse? RiskCapital { get; init; }

    /// <summary>The financial book's cursor (0 for the risk-weighted capital, which reads no book).</summary>
    [IgnoreMember]
    public long ProjectedLedgerSeq { get; private init; }

    public static AccountingFinancialReportIpcResponse From(AccountingFinancialReportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new AccountingFinancialReportIpcResponse
        {
            Currency = result.Currency,
            MinorUnits = AccountingFiat.MinorUnits(result.Currency),
            BalanceSheet = result.BalanceSheet is { } sheet ? AccountingFinancialBalanceSheetIpcResponse.From(sheet) : null,
            IncomeStatement = result.IncomeStatement is { } statement
                                  ? AccountingFinancialIncomeStatementIpcResponse.From(statement)
                                  : null,
            RealizedGains = result.RealizedGains is { } gains ? AccountingRealizedGainsIpcResponse.From(gains) : null,
            Lots = result.Lots is { } lots ? AccountingLotsIpcResponse.From(lots) : null,
            Register = result.Register is { } register ? AccountingFinancialRegisterIpcResponse.From(register) : null,
            Unvalued = result.Unvalued is { } unvalued ? AccountingUnvaluedIpcResponse.From(unvalued) : null,
            RiskCapital = result.RiskCapital is { } risk ? AccountingRiskCapitalIpcResponse.From(risk) : null,
            ProjectedLedgerSeq = result.BalanceSheet?.ProjectedLedgerSeq
                              ?? result.IncomeStatement?.ProjectedLedgerSeq
                              ?? result.Register?.ProjectedLedgerSeq
                              ?? result.Unvalued?.ProjectedLedgerSeq ?? 0
        };
    }

    internal static string Fiat(decimal value) => AccountingFiat.Format(value);

    internal static string? Fiat(decimal? value) => value is { } known ? AccountingFiat.Format(known) : null;

    internal static long? Milliseconds(DateTimeOffset? time) => time?.ToUnixTimeMilliseconds();
}

/// <summary>A BTC price a financial report used (NL-602 A3-T6).</summary>
[MessagePackObject]
public sealed class AccountingReportPriceIpcResponse
{
    [Key(0)] public required string PricePerBitcoin { get; init; }
    [Key(1)] public required string Currency { get; init; }

    /// <summary>The stored price's time, Unix milliseconds; null for a price the request gave.</summary>
    [Key(2)] public long? TimeUnixMilliseconds { get; init; }

    /// <summary>The stored price's id; null for a given price.</summary>
    [Key(3)] public long? PriceId { get; init; }

    public static AccountingReportPriceIpcResponse? From(AccountingReportPrice? price) => price is null
        ? null
        : new AccountingReportPriceIpcResponse
        {
            PricePerBitcoin = AccountingFinancialReportIpcResponse.Fiat(price.PricePerBitcoin),
            Currency = price.Currency,
            TimeUnixMilliseconds = AccountingFinancialReportIpcResponse.Milliseconds(price.Time),
            PriceId = price.PriceId
        };
}

/// <summary>One account of a financial report (NL-602 A3-T6), in the report's sign convention.</summary>
[MessagePackObject]
public sealed class AccountingFinancialAccountIpcResponse
{
    /// <summary>The <c>AccountRole</c> value its lines derive from.</summary>
    [Key(0)] public required int Account { get; init; }

    [Key(1)] public required string Role { get; init; }
    [Key(2)] public required string Name { get; init; }

    /// <summary>The <c>AccountingAccountCategory</c> name.</summary>
    [Key(3)] public required string Category { get; init; }

    [Key(4)] public required long AmountMsat { get; init; }
    [Key(5)] public required string FiatAmount { get; init; }

    /// <summary>Postings without a value in the report's currency (the fiat amount is partial while not 0).</summary>
    [Key(6)] public required int UnvaluedPostings { get; init; }

    /// <summary>An asset's value at the report's price.</summary>
    [Key(7)] public string? MarketValue { get; init; }

    public static AccountingFinancialAccountIpcResponse From(AccountingFinancialAccountLine line) => new()
    {
        Account = (int)line.Account,
        Role = line.Account.ToString(),
        Name = line.Name,
        Category = line.Category.ToString(),
        AmountMsat = line.AmountMsat,
        FiatAmount = AccountingFinancialReportIpcResponse.Fiat(line.FiatAmount),
        UnvaluedPostings = line.UnvaluedPostings,
        MarketValue = AccountingFinancialReportIpcResponse.Fiat(line.MarketValue)
    };
}

/// <summary>The financial balance sheet (NL-602 A3-T6).</summary>
[MessagePackObject]
public sealed class AccountingFinancialBalanceSheetIpcResponse
{
    [Key(0)] public required List<AccountingFinancialAccountIpcResponse> Assets { get; init; }
    [Key(1)] public required List<AccountingFinancialAccountIpcResponse> Liabilities { get; init; }
    [Key(2)] public required List<AccountingFinancialAccountIpcResponse> Equity { get; init; }
    [Key(3)] public required long RetainedEarningsMsat { get; init; }
    [Key(4)] public required string RetainedEarningsFiat { get; init; }
    [Key(5)] public required long TotalAssetsMsat { get; init; }
    [Key(6)] public required string TotalAssetsFiat { get; init; }
    [Key(7)] public required long TotalLiabilitiesMsat { get; init; }
    [Key(8)] public required string TotalLiabilitiesFiat { get; init; }
    [Key(9)] public required long TotalEquityMsat { get; init; }
    [Key(10)] public required string TotalEquityFiat { get; init; }
    [Key(11)] public required bool IsBalanced { get; init; }
    [Key(12)] public required bool IsFiatBalanced { get; init; }
    [Key(13)] public required int UnvaluedPostings { get; init; }
    [Key(14)] public AccountingReportPriceIpcResponse? Price { get; init; }
    [Key(15)] public string? TotalAssetsMarketValue { get; init; }

    public static AccountingFinancialBalanceSheetIpcResponse From(AccountingFinancialBalanceSheet sheet) => new()
    {
        Assets = sheet.Assets.Select(AccountingFinancialAccountIpcResponse.From).ToList(),
        Liabilities = sheet.Liabilities.Select(AccountingFinancialAccountIpcResponse.From).ToList(),
        Equity = sheet.Equity.Select(AccountingFinancialAccountIpcResponse.From).ToList(),
        RetainedEarningsMsat = sheet.RetainedEarningsMsat,
        RetainedEarningsFiat = AccountingFinancialReportIpcResponse.Fiat(sheet.RetainedEarningsFiat),
        TotalAssetsMsat = sheet.TotalAssetsMsat,
        TotalAssetsFiat = AccountingFinancialReportIpcResponse.Fiat(sheet.TotalAssetsFiat),
        TotalLiabilitiesMsat = sheet.TotalLiabilitiesMsat,
        TotalLiabilitiesFiat = AccountingFinancialReportIpcResponse.Fiat(sheet.TotalLiabilitiesFiat),
        TotalEquityMsat = sheet.TotalEquityMsat,
        TotalEquityFiat = AccountingFinancialReportIpcResponse.Fiat(sheet.TotalEquityFiat),
        IsBalanced = sheet.IsBalanced,
        IsFiatBalanced = sheet.IsFiatBalanced,
        UnvaluedPostings = sheet.UnvaluedPostings,
        Price = AccountingReportPriceIpcResponse.From(sheet.Price),
        TotalAssetsMarketValue = AccountingFinancialReportIpcResponse.Fiat(sheet.TotalAssetsMarketValue)
    };
}

/// <summary>The financial income statement (NL-602 A3-T6).</summary>
[MessagePackObject]
public sealed class AccountingFinancialIncomeStatementIpcResponse
{
    [Key(0)] public required List<AccountingFinancialAccountIpcResponse> Income { get; init; }
    [Key(1)] public required List<AccountingFinancialAccountIpcResponse> Expenses { get; init; }
    [Key(2)] public required long TotalIncomeMsat { get; init; }
    [Key(3)] public required string TotalIncomeFiat { get; init; }
    [Key(4)] public required long TotalExpensesMsat { get; init; }
    [Key(5)] public required string TotalExpensesFiat { get; init; }
    [Key(6)] public required long NetIncomeMsat { get; init; }
    [Key(7)] public required string NetIncomeFiat { get; init; }
    [Key(8)] public required int UnvaluedPostings { get; init; }

    public static AccountingFinancialIncomeStatementIpcResponse From(AccountingFinancialIncomeStatement statement) =>
        new()
        {
            Income = statement.Income.Select(AccountingFinancialAccountIpcResponse.From).ToList(),
            Expenses = statement.Expenses.Select(AccountingFinancialAccountIpcResponse.From).ToList(),
            TotalIncomeMsat = statement.TotalIncomeMsat,
            TotalIncomeFiat = AccountingFinancialReportIpcResponse.Fiat(statement.TotalIncomeFiat),
            TotalExpensesMsat = statement.TotalExpensesMsat,
            TotalExpensesFiat = AccountingFinancialReportIpcResponse.Fiat(statement.TotalExpensesFiat),
            NetIncomeMsat = statement.NetIncomeMsat,
            NetIncomeFiat = AccountingFinancialReportIpcResponse.Fiat(statement.NetIncomeFiat),
            UnvaluedPostings = statement.UnvaluedPostings
        };
}

/// <summary>The realized gains by period (NL-602 A3-T6).</summary>
[MessagePackObject]
public sealed class AccountingRealizedGainsIpcResponse
{
    /// <summary>The <c>AccountingGainsGrouping</c> value.</summary>
    [Key(0)] public required int Grouping { get; init; }

    [Key(1)] public required string GroupingName { get; init; }
    [Key(2)] public required List<AccountingRealizedGainsLineIpcResponse> Periods { get; init; }
    [Key(3)] public required AccountingRealizedGainsLineIpcResponse Total { get; init; }

    public static AccountingRealizedGainsIpcResponse From(AccountingRealizedGainsReport report) => new()
    {
        Grouping = (int)report.Grouping,
        GroupingName = report.Grouping.ToString(),
        Periods = report.Periods.Select(AccountingRealizedGainsLineIpcResponse.From).ToList(),
        Total = AccountingRealizedGainsLineIpcResponse.From(report.Total)
    };
}

/// <summary>The realized gains of one period (NL-602 A3-T6).</summary>
[MessagePackObject]
public sealed class AccountingRealizedGainsLineIpcResponse
{
    [Key(0)] public required string Period { get; init; }
    [Key(1)] public long? StartUnixMilliseconds { get; init; }
    [Key(2)] public long? EndUnixMilliseconds { get; init; }
    [Key(3)] public required int Reliefs { get; init; }
    [Key(4)] public required long DisposedMsat { get; init; }
    [Key(5)] public required string CostBasis { get; init; }
    [Key(6)] public required string Proceeds { get; init; }
    [Key(7)] public required string Gain { get; init; }
    [Key(8)] public required string ShortTermGain { get; init; }
    [Key(9)] public required string LongTermGain { get; init; }
    [Key(10)] public required int PendingValuation { get; init; }
    [Key(11)] public required long PendingValuationMsat { get; init; }
    [Key(12)] public required long BasisEstimatedMsat { get; init; }

    public static AccountingRealizedGainsLineIpcResponse From(AccountingRealizedGainsLine line) => new()
    {
        Period = line.Period,
        StartUnixMilliseconds = AccountingFinancialReportIpcResponse.Milliseconds(line.Start),
        EndUnixMilliseconds = AccountingFinancialReportIpcResponse.Milliseconds(line.End),
        Reliefs = line.Reliefs,
        DisposedMsat = line.DisposedMsat,
        CostBasis = AccountingFinancialReportIpcResponse.Fiat(line.CostBasis),
        Proceeds = AccountingFinancialReportIpcResponse.Fiat(line.Proceeds),
        Gain = AccountingFinancialReportIpcResponse.Fiat(line.Gain),
        ShortTermGain = AccountingFinancialReportIpcResponse.Fiat(line.ShortTermGain),
        LongTermGain = AccountingFinancialReportIpcResponse.Fiat(line.LongTermGain),
        PendingValuation = line.PendingValuation,
        PendingValuationMsat = line.PendingValuationMsat,
        BasisEstimatedMsat = line.BasisEstimatedMsat
    };
}

/// <summary>The open lots, with their unrealized gains at a price (NL-602 A3-T6).</summary>
[MessagePackObject]
public sealed class AccountingLotsIpcResponse
{
    [Key(0)] public AccountingReportPriceIpcResponse? Price { get; init; }
    [Key(1)] public required List<AccountingLotIpcResponse> Lots { get; init; }
    [Key(2)] public required long NextAfterLotId { get; init; }
    [Key(3)] public required bool HasMore { get; init; }
    [Key(4)] public required int OpenLots { get; init; }
    [Key(5)] public required long RemainingMsat { get; init; }
    [Key(6)] public required string CostBasis { get; init; }
    [Key(7)] public required long UnvaluedMsat { get; init; }
    [Key(8)] public required int UnvaluedLots { get; init; }
    [Key(9)] public required long BasisEstimatedMsat { get; init; }
    [Key(10)] public string? MarketValue { get; init; }
    [Key(11)] public string? UnrealizedGain { get; init; }

    public static AccountingLotsIpcResponse From(AccountingLotsReport report) => new()
    {
        Price = AccountingReportPriceIpcResponse.From(report.Price),
        Lots = report.Lots.Select(AccountingLotIpcResponse.From).ToList(),
        NextAfterLotId = report.NextAfterLotId,
        HasMore = report.HasMore,
        OpenLots = report.Totals.OpenLots,
        RemainingMsat = report.Totals.RemainingMsat,
        CostBasis = AccountingFinancialReportIpcResponse.Fiat(report.Totals.CostBasis),
        UnvaluedMsat = report.Totals.UnvaluedMsat,
        UnvaluedLots = report.Totals.UnvaluedLots,
        BasisEstimatedMsat = report.Totals.BasisEstimatedMsat,
        MarketValue = AccountingFinancialReportIpcResponse.Fiat(report.Totals.MarketValue),
        UnrealizedGain = AccountingFinancialReportIpcResponse.Fiat(report.Totals.UnrealizedGain)
    };
}

/// <summary>One open lot (NL-602 A3-T6).</summary>
[MessagePackObject]
public sealed class AccountingLotIpcResponse
{
    [Key(0)] public required long Id { get; init; }
    [Key(1)] public required long AcquiredAtUnixMilliseconds { get; init; }

    /// <summary>The <c>AccountingLotOrigin</c> name.</summary>
    [Key(2)] public required string Origin { get; init; }

    [Key(3)] public long? SourceLedgerSeq { get; init; }

    /// <summary>The <c>AccountingLotBucket</c> name of the bucket that holds it (<c>Channels</c>, <c>Wallet</c>,
    /// <c>HeldOutside</c>, ...), or null for a lot of the node-wide pool of an older book (NL-657).</summary>
    [Key(4)] public string? Account { get; init; }

    [Key(5)] public required long OriginalMsat { get; init; }
    [Key(6)] public required long RemainingMsat { get; init; }
    [Key(7)] public string? FiatCost { get; init; }
    [Key(8)] public string? FiatCurrency { get; init; }
    [Key(9)] public required bool BasisEstimated { get; init; }
    [Key(10)] public string? ClosedPeriodId { get; init; }
    [Key(11)] public string? RemainingCostBasis { get; init; }
    [Key(12)] public string? MarketValue { get; init; }
    [Key(13)] public string? UnrealizedGain { get; init; }

    /// <summary>When the sats of a part moved from another lot were acquired (its holding period starts there), or null
    /// for the lot's own acquisition time (NL-657).</summary>
    [Key(14)] public long? HeldSinceUnixMilliseconds { get; init; }

    /// <summary>The lot a moved part came from, or null (NL-657).</summary>
    [Key(15)] public long? ParentLotId { get; init; }

    public static AccountingLotIpcResponse From(AccountingLotLine line) => new()
    {
        Id = line.Lot.Id,
        AcquiredAtUnixMilliseconds = line.Lot.AcquiredAt.ToUnixTimeMilliseconds(),
        Origin = line.Lot.Origin.ToString(),
        SourceLedgerSeq = line.Lot.SourceLedgerSeq,
        Account = line.Lot.Bucket?.ToString(),
        HeldSinceUnixMilliseconds = line.Lot.HeldSince?.ToUnixTimeMilliseconds(),
        ParentLotId = line.Lot.ParentLotId,
        OriginalMsat = line.Lot.OriginalMsat,
        RemainingMsat = line.Lot.RemainingMsat,
        FiatCost = AccountingFinancialReportIpcResponse.Fiat(line.Lot.FiatCost),
        FiatCurrency = line.Lot.FiatCurrency,
        BasisEstimated = line.Lot.BasisEstimated,
        ClosedPeriodId = line.Lot.ClosedPeriodId,
        RemainingCostBasis = AccountingFinancialReportIpcResponse.Fiat(line.RemainingCostBasis),
        MarketValue = AccountingFinancialReportIpcResponse.Fiat(line.MarketValue),
        UnrealizedGain = AccountingFinancialReportIpcResponse.Fiat(line.UnrealizedGain)
    };
}

/// <summary>A page of the financial book's entries (NL-602 A3-T6).</summary>
[MessagePackObject]
public sealed class AccountingFinancialRegisterIpcResponse
{
    [Key(0)] public required List<AccountingFinancialEntryIpcResponse> Entries { get; init; }

    /// <summary>The next page's cursor: pass it with <see cref="NextAfterAdjustment"/>.</summary>
    [Key(1)] public required long NextAfter { get; init; }

    [Key(2)] public required int NextAfterAdjustment { get; init; }
    [Key(3)] public required bool HasMore { get; init; }

    public static AccountingFinancialRegisterIpcResponse From(AccountingFinancialRegister register) => new()
    {
        Entries = register.Entries.Select(AccountingFinancialEntryIpcResponse.From).ToList(),
        NextAfter = register.NextAfter,
        NextAfterAdjustment = register.NextAfterAdjustment,
        HasMore = register.HasMore
    };
}

/// <summary>One entry of the financial book with its lines (NL-602 A3-T6).</summary>
[MessagePackObject]
public sealed class AccountingFinancialEntryIpcResponse
{
    [Key(0)] public required long LedgerSeq { get; init; }
    [Key(1)] public required int Adjustment { get; init; }
    [Key(2)] public required string EventKey { get; init; }
    [Key(3)] public required int Kind { get; init; }
    [Key(4)] public required string KindName { get; init; }
    [Key(5)] public required long OccurredAtUnixMilliseconds { get; init; }
    [Key(6)] public string? ChannelId { get; init; }
    [Key(7)] public string? PaymentHash { get; init; }

    /// <summary>The <c>AccountingEntryFlags</c> value and its names.</summary>
    [Key(8)] public required int Flags { get; init; }

    [Key(9)] public required string FlagNames { get; init; }

    /// <summary>The <c>AccountingClassificationSource</c> name, or null.</summary>
    [Key(10)] public string? Classification { get; init; }

    [Key(11)] public long? RuleId { get; init; }
    [Key(12)] public string? ClosedPeriodId { get; init; }
    [Key(13)] public string? Note { get; init; }
    [Key(14)] public required List<AccountingFinancialPostingIpcResponse> Postings { get; init; }

    public static AccountingFinancialEntryIpcResponse From(AccountingEntry entry) => new()
    {
        LedgerSeq = entry.LedgerSeq,
        Adjustment = entry.Adjustment,
        EventKey = entry.EventKey,
        Kind = (int)entry.Kind,
        KindName = entry.Kind.ToString(),
        OccurredAtUnixMilliseconds = entry.OccurredAt.ToUnixTimeMilliseconds(),
        ChannelId = entry.ChannelId?.ToString(),
        PaymentHash = entry.PaymentHash?.ToString(),
        Flags = (int)entry.Flags,
        FlagNames = entry.Flags.ToString(),
        Classification = entry.Classification?.ToString(),
        RuleId = entry.RuleId,
        ClosedPeriodId = entry.ClosedPeriodId,
        Note = entry.Note,
        Postings = entry.Postings.Select(p => new AccountingFinancialPostingIpcResponse
        {
            Account = (int)p.Account,
            Role = p.Account.ToString(),
            Name = p.AccountName ?? AccountNames.Default[p.Account],
            AmountMsat = p.AmountMsat,
            FiatAmount = AccountingFinancialReportIpcResponse.Fiat(p.FiatAmount),
            FiatCurrency = p.FiatCurrency,
            PriceId = p.PriceId
        }).ToList()
    };
}

/// <summary>One line of a financial entry (NL-602 A3-T6): debit positive, credit negative.</summary>
[MessagePackObject]
public sealed class AccountingFinancialPostingIpcResponse
{
    [Key(0)] public required int Account { get; init; }
    [Key(1)] public required string Role { get; init; }
    [Key(2)] public required string Name { get; init; }
    [Key(3)] public required long AmountMsat { get; init; }
    [Key(4)] public string? FiatAmount { get; init; }
    [Key(5)] public string? FiatCurrency { get; init; }
    [Key(6)] public long? PriceId { get; init; }
}

/// <summary>The oldest postings without a fiat value (NL-602 A3-T6).</summary>
[MessagePackObject]
public sealed class AccountingUnvaluedIpcResponse
{
    [Key(0)] public required List<AccountingUnvaluedPostingIpcResponse> Postings { get; init; }
    [Key(1)] public required bool HasMore { get; init; }

    public static AccountingUnvaluedIpcResponse From(AccountingUnvaluedReport report) => new()
    {
        Postings = report.Postings.Select(p => new AccountingUnvaluedPostingIpcResponse
        {
            LedgerSeq = p.Key.LedgerSeq,
            Adjustment = p.Key.Adjustment,
            Index = p.Key.Index,
            OccurredAtUnixMilliseconds = p.OccurredAt.ToUnixTimeMilliseconds(),
            Account = (int)p.Account,
            Role = p.Account.ToString(),
            Name = p.AccountName ?? AccountNames.Default[p.Account],
            AmountMsat = p.AmountMsat,
            ClosedPeriodId = p.ClosedPeriodId
        }).ToList(),
        HasMore = report.HasMore
    };
}

/// <summary>One posting without a fiat value (NL-602 A3-T6).</summary>
[MessagePackObject]
public sealed class AccountingUnvaluedPostingIpcResponse
{
    [Key(0)] public required long LedgerSeq { get; init; }
    [Key(1)] public required int Adjustment { get; init; }
    [Key(2)] public required int Index { get; init; }
    [Key(3)] public required long OccurredAtUnixMilliseconds { get; init; }
    [Key(4)] public required int Account { get; init; }
    [Key(5)] public required string Role { get; init; }
    [Key(6)] public required string Name { get; init; }
    [Key(7)] public required long AmountMsat { get; init; }
    [Key(8)] public string? ClosedPeriodId { get; init; }
}

/// <summary>The risk-weighted capital of a live snapshot (NL-602 A3-T6, plan §6.2 "Audit").</summary>
[MessagePackObject]
public sealed class AccountingRiskCapitalIpcResponse
{
    [Key(0)] public required long TakenAtUnixMilliseconds { get; init; }
    [Key(1)] public required uint BlockHeight { get; init; }
    [Key(2)] public required List<AccountingRiskCapitalLineIpcResponse> Lines { get; init; }
    [Key(3)] public required List<AccountingRiskCapitalChannelIpcResponse> Channels { get; init; }
    [Key(4)] public required long GrossMsat { get; init; }
    [Key(5)] public required long WeightedMsat { get; init; }
    [Key(6)] public string? WeightedFiat { get; init; }

    /// <summary>The peers' balances, left out.</summary>
    [Key(7)] public required long ExcludedRemoteMsat { get; init; }

    [Key(8)] public AccountingReportPriceIpcResponse? Price { get; init; }

    public static AccountingRiskCapitalIpcResponse From(AccountingRiskCapitalReport report) => new()
    {
        TakenAtUnixMilliseconds = report.TakenAt.ToUnixTimeMilliseconds(),
        BlockHeight = report.BlockHeight,
        Lines = report.Lines.Select(l => new AccountingRiskCapitalLineIpcResponse
        {
            State = l.State,
            AmountMsat = l.AmountMsat,
            Weight = AccountingFinancialReportIpcResponse.Fiat(l.Weight),
            WeightedMsat = l.WeightedMsat
        }).ToList(),
        Channels = report.Channels.Select(c => new AccountingRiskCapitalChannelIpcResponse
        {
            ChannelId = c.ChannelId.ToString(),
            ShortChannelId = c.ShortChannelId?.ToString(),
            State = c.State.ToString(),
            Counterparty = c.Counterparty?.ToString(),
            SettledMsat = c.SettledMsat,
            OutgoingInFlightMsat = c.OutgoingInFlightMsat,
            OutgoingFulfilledMsat = c.OutgoingFulfilledMsat,
            IncomingWithPreimageMsat = c.IncomingWithPreimageMsat,
            IncomingWithoutPreimageMsat = c.IncomingWithoutPreimageMsat,
            RemoteMsat = c.RemoteMsat,
            PendingOnchainMsat = c.PendingOnchainMsat,
            PendingHtlcOnchainMsat = c.PendingHtlcOnchainMsat,
            PendingUncountedMsat = c.PendingUncountedMsat,
            WeightedMsat = c.WeightedMsat
        }).ToList(),
        GrossMsat = report.GrossMsat,
        WeightedMsat = report.WeightedMsat,
        WeightedFiat = AccountingFinancialReportIpcResponse.Fiat(report.WeightedFiat),
        ExcludedRemoteMsat = report.ExcludedRemoteMsat,
        Price = AccountingReportPriceIpcResponse.From(report.Price)
    };
}

/// <summary>One settlement state of the risk-weighted view (NL-602 A3-T6).</summary>
[MessagePackObject]
public sealed class AccountingRiskCapitalLineIpcResponse
{
    [Key(0)] public required string State { get; init; }
    [Key(1)] public required long AmountMsat { get; init; }
    [Key(2)] public required string Weight { get; init; }
    [Key(3)] public required long WeightedMsat { get; init; }
}

/// <summary>One channel of the risk-weighted view (NL-602 A3-T6).</summary>
[MessagePackObject]
public sealed class AccountingRiskCapitalChannelIpcResponse
{
    [Key(0)] public required string ChannelId { get; init; }
    [Key(1)] public string? ShortChannelId { get; init; }
    [Key(2)] public required string State { get; init; }
    [Key(3)] public string? Counterparty { get; init; }
    [Key(4)] public required long SettledMsat { get; init; }
    [Key(5)] public required long OutgoingInFlightMsat { get; init; }
    [Key(6)] public required long OutgoingFulfilledMsat { get; init; }
    [Key(7)] public required long IncomingWithPreimageMsat { get; init; }
    [Key(8)] public required long IncomingWithoutPreimageMsat { get; init; }
    [Key(9)] public required long RemoteMsat { get; init; }
    [Key(10)] public required long PendingOnchainMsat { get; init; }
    [Key(11)] public required long PendingHtlcOnchainMsat { get; init; }
    [Key(12)] public required long PendingUncountedMsat { get; init; }
    [Key(13)] public required long WeightedMsat { get; init; }
}