using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Accounting.Books;
using Domain.Accounting.Books.Reports;
using Domain.Client.Responses;

/// <summary>
/// Response for AccountingReport (ClientCommand 43, NL-602 A2): the report of the kind asked for (the others null).
/// Amounts are msat; keys are append-only.
/// </summary>
[MessagePackObject]
public sealed class AccountingReportIpcResponse
{
    /// <summary>The <c>AccountingReportKind</c> value.</summary>
    [Key(0)] public required int Kind { get; init; }

    /// <summary>The <c>AccountingReportKind</c> name.</summary>
    [Key(1)] public required string KindName { get; init; }

    /// <summary>The books' cursor: the last event the report includes.</summary>
    [Key(2)] public required long ProjectedLedgerSeq { get; init; }

    /// <summary>The start of the period, Unix milliseconds.</summary>
    [Key(3)] public long? SinceUnixMilliseconds { get; init; }

    /// <summary>The end of the period (or the balance sheet's time), Unix milliseconds.</summary>
    [Key(4)] public long? UntilUnixMilliseconds { get; init; }

    [Key(5)] public AccountingBalanceSheetIpcResponse? BalanceSheet { get; init; }
    [Key(6)] public AccountingIncomeStatementIpcResponse? IncomeStatement { get; init; }

    /// <summary>The channels view (kind channels).</summary>
    [Key(7)] public List<AccountingChannelIpcResponse>? Channels { get; init; }

    /// <summary>The per-peer summary (kind peers).</summary>
    [Key(8)] public List<AccountingPeerIpcResponse>? Peers { get; init; }

    [Key(9)] public AccountingFeesIpcResponse? Fees { get; init; }
    [Key(10)] public AccountingRegisterIpcResponse? Register { get; init; }

    /// <summary>The time the yields' open periods end at (channels and peers), Unix milliseconds.</summary>
    [Key(11)] public long? AsOfUnixMilliseconds { get; init; }

    public static AccountingReportIpcResponse FromClientResponse(AccountingReportClientResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        long? since = null, until = null, asOf = null;
        long cursor = 0;
        AccountingBalanceSheetIpcResponse? balanceSheet = null;
        AccountingIncomeStatementIpcResponse? incomeStatement = null;
        List<AccountingChannelIpcResponse>? channels = null;
        List<AccountingPeerIpcResponse>? peers = null;
        AccountingFeesIpcResponse? fees = null;
        AccountingRegisterIpcResponse? register = null;
        if (response.BalanceSheet is { } sheet)
        {
            cursor = sheet.ProjectedLedgerSeq;
            until = Milliseconds(sheet.At);
            balanceSheet = AccountingBalanceSheetIpcResponse.From(sheet);
        }

        if (response.IncomeStatement is { } statement)
        {
            cursor = statement.ProjectedLedgerSeq;
            (since, until) = (Milliseconds(statement.Since), Milliseconds(statement.Until));
            incomeStatement = AccountingIncomeStatementIpcResponse.From(statement);
        }

        if (response.Channels is { } view)
        {
            cursor = view.ProjectedLedgerSeq;
            (since, until, asOf) = (Milliseconds(view.Since), Milliseconds(view.Until), view.AsOf.ToUnixTimeMilliseconds());
            if (response.Kind == AccountingReportKind.Peers)
                peers = view.Peers.Select(AccountingPeerIpcResponse.From).ToList();
            else
                channels = view.Channels.Select(AccountingChannelIpcResponse.From).ToList();
        }

        if (response.Fees is { } feeReport)
        {
            cursor = feeReport.ProjectedLedgerSeq;
            (since, until) = (Milliseconds(feeReport.Since), Milliseconds(feeReport.Until));
            fees = AccountingFeesIpcResponse.From(feeReport);
        }

        if (response.Register is { } page)
        {
            cursor = page.ProjectedLedgerSeq;
            register = AccountingRegisterIpcResponse.From(page, response.Names);
        }

        return new AccountingReportIpcResponse
        {
            Kind = (int)response.Kind,
            KindName = response.Kind.ToString(),
            ProjectedLedgerSeq = cursor,
            SinceUnixMilliseconds = since,
            UntilUnixMilliseconds = until,
            AsOfUnixMilliseconds = asOf,
            BalanceSheet = balanceSheet,
            IncomeStatement = incomeStatement,
            Channels = channels,
            Peers = peers,
            Fees = fees,
            Register = register
        };
    }

    internal static long? Milliseconds(DateTimeOffset? time) => time?.ToUnixTimeMilliseconds();
}

/// <summary>One account of a report (NL-602 A2).</summary>
[MessagePackObject]
public sealed class AccountingAccountIpcResponse
{
    /// <summary>The <c>AccountRole</c> value.</summary>
    [Key(0)] public required int Account { get; init; }

    /// <summary>The <c>AccountRole</c> name.</summary>
    [Key(1)] public required string Role { get; init; }

    /// <summary>The account name in effect.</summary>
    [Key(2)] public required string Name { get; init; }

    /// <summary>The amount in msat, in the report's sign convention.</summary>
    [Key(3)] public required long AmountMsat { get; init; }

    public static AccountingAccountIpcResponse From(AccountingAccountLine line) => new()
    {
        Account = (int)line.Account,
        Role = line.Account.ToString(),
        Name = line.Name,
        AmountMsat = line.AmountMsat
    };
}

/// <summary>The balance sheet: assets against liabilities, equity and the period's earnings (NL-602 A2).</summary>
[MessagePackObject]
public sealed class AccountingBalanceSheetIpcResponse
{
    /// <summary>Asset accounts, debit-positive.</summary>
    [Key(0)] public required List<AccountingAccountIpcResponse> Assets { get; init; }

    /// <summary>Liability accounts, credit-positive.</summary>
    [Key(1)] public required List<AccountingAccountIpcResponse> Liabilities { get; init; }

    /// <summary>Equity accounts, credit-positive.</summary>
    [Key(2)] public required List<AccountingAccountIpcResponse> Equity { get; init; }

    /// <summary>The current period's net income (income less expenses).</summary>
    [Key(3)] public required long RetainedEarningsMsat { get; init; }

    [Key(4)] public required long TotalAssetsMsat { get; init; }
    [Key(5)] public required long TotalLiabilitiesMsat { get; init; }
    [Key(6)] public required long TotalEquityMsat { get; init; }

    /// <summary>Whether assets = liabilities + equity + earnings.</summary>
    [Key(7)] public required bool IsBalanced { get; init; }

    /// <summary>
    /// For a balance at a past time: the highest ledger sequence of the entries it counts, 0 when none (NL-627); null
    /// for a balance of now.
    /// </summary>
    [Key(8)] public long? LastLedgerSeqAt { get; init; }

    public static AccountingBalanceSheetIpcResponse From(AccountingBalanceSheet sheet) => new()
    {
        Assets = sheet.Assets.Select(AccountingAccountIpcResponse.From).ToList(),
        Liabilities = sheet.Liabilities.Select(AccountingAccountIpcResponse.From).ToList(),
        Equity = sheet.Equity.Select(AccountingAccountIpcResponse.From).ToList(),
        RetainedEarningsMsat = sheet.RetainedEarningsMsat,
        TotalAssetsMsat = sheet.TotalAssetsMsat,
        TotalLiabilitiesMsat = sheet.TotalLiabilitiesMsat,
        TotalEquityMsat = sheet.TotalEquityMsat,
        IsBalanced = sheet.IsBalanced,
        LastLedgerSeqAt = sheet.LastLedgerSeqAt
    };
}

/// <summary>The income statement of a period (NL-602 A2).</summary>
[MessagePackObject]
public sealed class AccountingIncomeStatementIpcResponse
{
    /// <summary>Income accounts, credit-positive.</summary>
    [Key(0)] public required List<AccountingAccountIpcResponse> Income { get; init; }

    /// <summary>Expense accounts, debit-positive.</summary>
    [Key(1)] public required List<AccountingAccountIpcResponse> Expenses { get; init; }

    [Key(2)] public required long TotalIncomeMsat { get; init; }
    [Key(3)] public required long TotalExpensesMsat { get; init; }
    [Key(4)] public required long NetIncomeMsat { get; init; }

    public static AccountingIncomeStatementIpcResponse From(AccountingIncomeStatement statement) => new()
    {
        Income = statement.Income.Select(AccountingAccountIpcResponse.From).ToList(),
        Expenses = statement.Expenses.Select(AccountingAccountIpcResponse.From).ToList(),
        TotalIncomeMsat = statement.TotalIncomeMsat,
        TotalExpensesMsat = statement.TotalExpensesMsat,
        NetIncomeMsat = statement.NetIncomeMsat
    };
}

/// <summary>One channel of the channels view (NL-602 A2); amounts msat.</summary>
[MessagePackObject]
public sealed class AccountingChannelIpcResponse
{
    [Key(0)] public required string ChannelId { get; init; }
    [Key(1)] public string? ShortChannelId { get; init; }
    [Key(2)] public string? Counterparty { get; init; }
    [Key(3)] public long? CapacityMsat { get; init; }
    [Key(4)] public bool? IsInitiator { get; init; }
    [Key(5)] public long? OpenedAtUnixMilliseconds { get; init; }
    [Key(6)] public long? ClosedAtUnixMilliseconds { get; init; }
    [Key(7)] public long RoutingInMsat { get; init; }
    [Key(8)] public long RoutingOutMsat { get; init; }
    [Key(9)] public int ForwardsIn { get; init; }
    [Key(10)] public int ForwardsOut { get; init; }
    [Key(11)] public long ForwardedInMsat { get; init; }
    [Key(12)] public long ForwardedOutMsat { get; init; }
    [Key(13)] public long PaymentsSentMsat { get; init; }
    [Key(14)] public int PaymentsSent { get; init; }
    [Key(15)] public long PaymentsReceivedMsat { get; init; }
    [Key(16)] public int PaymentsReceived { get; init; }
    [Key(17)] public long RoutingFeesPaidMsat { get; init; }
    [Key(18)] public long RebalancedOutMsat { get; init; }
    [Key(19)] public long RebalanceCostMsat { get; init; }

    /// <summary>The push at the open: positive from the peer, negative from us.</summary>
    [Key(20)] public long PushMsat { get; init; }

    [Key(21)] public long FundingFeeMsat { get; init; }
    [Key(22)] public long SpliceFeeMsat { get; init; }
    [Key(23)] public long CloseFeeMsat { get; init; }
    [Key(24)] public long CommitmentFeeMsat { get; init; }
    [Key(25)] public long SweepFeeMsat { get; init; }
    [Key(26)] public long CpfpFeeMsat { get; init; }
    [Key(27)] public long OnchainLossMsat { get; init; }
    [Key(28)] public long OnchainFeesMsat { get; init; }

    /// <summary>Routing out less rebalance cost, on-chain fees and losses.</summary>
    [Key(29)] public long NetMsat { get; init; }

    /// <summary>Routing out over capacity (a fraction): the routing yield before costs (NL-625).</summary>
    [Key(30)] public double? YieldOnCapacity { get; init; }

    /// <summary>The routing yield annualized over the time open within the period (a fraction).</summary>
    [Key(31)] public double? AnnualizedYield { get; init; }

    /// <summary>The block the funding confirmed in, when known (NL-623).</summary>
    [Key(32)] public uint? OpenedAtBlockHeight { get; init; }

    /// <summary>When the feed began following a channel open before it, Unix milliseconds (NL-623).</summary>
    [Key(33)] public long? TrackedSinceUnixMilliseconds { get; init; }

    /// <summary>Net over capacity (a fraction, negative for a loss; NL-625).</summary>
    [Key(34)] public double? NetYieldOnCapacity { get; init; }

    /// <summary>The net yield annualized over the same time as <see cref="AnnualizedYield"/>.</summary>
    [Key(35)] public double? NetAnnualizedYield { get; init; }

    public static AccountingChannelIpcResponse From(AccountingChannelLine line) => new()
    {
        ChannelId = line.ChannelId.ToString(),
        ShortChannelId = line.ShortChannelId,
        Counterparty = line.Counterparty?.ToString(),
        CapacityMsat = line.CapacityMsat,
        IsInitiator = line.IsInitiator,
        OpenedAtUnixMilliseconds = AccountingReportIpcResponse.Milliseconds(line.OpenedAt),
        ClosedAtUnixMilliseconds = AccountingReportIpcResponse.Milliseconds(line.ClosedAt),
        RoutingInMsat = line.RoutingInMsat,
        RoutingOutMsat = line.RoutingOutMsat,
        ForwardsIn = line.ForwardsIn,
        ForwardsOut = line.ForwardsOut,
        ForwardedInMsat = line.ForwardedInMsat,
        ForwardedOutMsat = line.ForwardedOutMsat,
        PaymentsSentMsat = line.PaymentsSentMsat,
        PaymentsSent = line.PaymentsSent,
        PaymentsReceivedMsat = line.PaymentsReceivedMsat,
        PaymentsReceived = line.PaymentsReceived,
        RoutingFeesPaidMsat = line.RoutingFeesPaidMsat,
        RebalancedOutMsat = line.RebalancedOutMsat,
        RebalanceCostMsat = line.RebalanceCostMsat,
        PushMsat = line.PushMsat,
        FundingFeeMsat = line.FundingFeeMsat,
        SpliceFeeMsat = line.SpliceFeeMsat,
        CloseFeeMsat = line.CloseFeeMsat,
        CommitmentFeeMsat = line.CommitmentFeeMsat,
        SweepFeeMsat = line.SweepFeeMsat,
        CpfpFeeMsat = line.CpfpFeeMsat,
        OnchainLossMsat = line.OnchainLossMsat,
        OnchainFeesMsat = line.OnchainFeesMsat,
        NetMsat = line.NetMsat,
        YieldOnCapacity = line.YieldOnCapacity,
        AnnualizedYield = line.AnnualizedYield,
        OpenedAtBlockHeight = line.OpenedAtBlockHeight,
        TrackedSinceUnixMilliseconds = AccountingReportIpcResponse.Milliseconds(line.TrackedSince),
        NetYieldOnCapacity = line.NetYieldOnCapacity,
        NetAnnualizedYield = line.NetAnnualizedYield
    };
}

/// <summary>The channels of one peer summed (NL-602 A2); amounts msat.</summary>
[MessagePackObject]
public sealed class AccountingPeerIpcResponse
{
    /// <summary>The peer, or null for channels whose peer is not in the feed.</summary>
    [Key(0)] public string? Counterparty { get; init; }

    [Key(1)] public int ChannelCount { get; init; }
    [Key(2)] public long CapacityMsat { get; init; }
    [Key(3)] public long RoutingInMsat { get; init; }
    [Key(4)] public long RoutingOutMsat { get; init; }
    [Key(5)] public int ForwardsIn { get; init; }
    [Key(6)] public int ForwardsOut { get; init; }
    [Key(7)] public long PaymentsSentMsat { get; init; }
    [Key(8)] public long PaymentsReceivedMsat { get; init; }
    [Key(9)] public long RoutingFeesPaidMsat { get; init; }
    [Key(10)] public long RebalanceCostMsat { get; init; }
    [Key(11)] public long OnchainFeesMsat { get; init; }
    [Key(12)] public long OnchainLossMsat { get; init; }
    [Key(13)] public long NetMsat { get; init; }

    public static AccountingPeerIpcResponse From(AccountingPeerLine line) => new()
    {
        Counterparty = line.Counterparty?.ToString(),
        ChannelCount = line.ChannelCount,
        CapacityMsat = line.CapacityMsat,
        RoutingInMsat = line.RoutingInMsat,
        RoutingOutMsat = line.RoutingOutMsat,
        ForwardsIn = line.ForwardsIn,
        ForwardsOut = line.ForwardsOut,
        PaymentsSentMsat = line.PaymentsSentMsat,
        PaymentsReceivedMsat = line.PaymentsReceivedMsat,
        RoutingFeesPaidMsat = line.RoutingFeesPaidMsat,
        RebalanceCostMsat = line.RebalanceCostMsat,
        OnchainFeesMsat = line.OnchainFeesMsat,
        OnchainLossMsat = line.OnchainLossMsat,
        NetMsat = line.NetMsat
    };
}

/// <summary>The fee breakdown of a period (NL-602 A2).</summary>
[MessagePackObject]
public sealed class AccountingFeesIpcResponse
{
    /// <summary>Every fee expense account, debit-positive.</summary>
    [Key(0)] public required List<AccountingAccountIpcResponse> Accounts { get; init; }

    [Key(1)] public required long TotalMsat { get; init; }

    /// <summary>The confirmed sweep replacements (informational: already in the sweep fee account).</summary>
    [Key(2)] public required List<AccountingSweepFeeBumpIpcResponse> SweepFeeBumps { get; init; }

    [Key(3)] public required long SweepFeeBumpTotalMsat { get; init; }

    public static AccountingFeesIpcResponse From(AccountingFeesReport report) => new()
    {
        Accounts = report.Accounts.Select(AccountingAccountIpcResponse.From).ToList(),
        TotalMsat = report.TotalMsat,
        SweepFeeBumps = report.SweepFeeBumps.Select(b => new AccountingSweepFeeBumpIpcResponse
        {
            LedgerSeq = b.LedgerSeq,
            OccurredAtUnixMilliseconds = b.OccurredAt.ToUnixTimeMilliseconds(),
            TxId = b.TxId?.ToString(),
            ChannelId = b.ChannelId?.ToString(),
            FeeMsat = b.FeeMsat,
            Purpose = b.Purpose
        }).ToList(),
        SweepFeeBumpTotalMsat = report.SweepFeeBumpTotalMsat
    };
}

/// <summary>A confirmed sweep replacement (NL-602 A2).</summary>
[MessagePackObject]
public sealed class AccountingSweepFeeBumpIpcResponse
{
    [Key(0)] public required long LedgerSeq { get; init; }
    [Key(1)] public required long OccurredAtUnixMilliseconds { get; init; }

    /// <summary>The replacement, in display order.</summary>
    [Key(2)] public string? TxId { get; init; }

    [Key(3)] public string? ChannelId { get; init; }
    [Key(4)] public required long FeeMsat { get; init; }
    [Key(5)] public string? Purpose { get; init; }
}

/// <summary>A page of the register (NL-602 A2).</summary>
[MessagePackObject]
public sealed class AccountingRegisterIpcResponse
{
    /// <summary>The entries in ledger order.</summary>
    [Key(0)] public required List<AccountingEntryIpcResponse> Entries { get; init; }

    /// <summary>The cursor of the next page.</summary>
    [Key(1)] public required long NextAfter { get; init; }

    /// <summary>Whether the page is full.</summary>
    [Key(2)] public required bool HasMore { get; init; }

    public static AccountingRegisterIpcResponse From(AccountingRegister register, AccountNames names) => new()
    {
        Entries = register.Entries.Select(e => AccountingEntryIpcResponse.From(e, names)).ToList(),
        NextAfter = register.NextAfter,
        HasMore = register.HasMore
    };
}

/// <summary>One entry of the books with its postings (NL-602 A2).</summary>
[MessagePackObject]
public sealed class AccountingEntryIpcResponse
{
    [Key(0)] public required long LedgerSeq { get; init; }
    [Key(1)] public required string EventKey { get; init; }

    /// <summary>The <c>AccountingEventKind</c> value.</summary>
    [Key(2)] public required int Kind { get; init; }

    [Key(3)] public required string KindName { get; init; }
    [Key(4)] public required long OccurredAtUnixMilliseconds { get; init; }
    [Key(5)] public string? ChannelId { get; init; }
    [Key(6)] public string? PaymentHash { get; init; }

    /// <summary>The postings (debit positive, credit negative), summing to zero.</summary>
    [Key(7)] public required List<AccountingAccountIpcResponse> Postings { get; init; }

    [Key(8)] public string? Note { get; init; }

    public static AccountingEntryIpcResponse From(AccountingEntry entry, AccountNames names) => new()
    {
        LedgerSeq = entry.LedgerSeq,
        EventKey = entry.EventKey,
        Kind = (int)entry.Kind,
        KindName = entry.Kind.ToString(),
        OccurredAtUnixMilliseconds = entry.OccurredAt.ToUnixTimeMilliseconds(),
        ChannelId = entry.ChannelId?.ToString(),
        PaymentHash = entry.PaymentHash?.ToString(),
        Postings = entry.Postings.Select(p => new AccountingAccountIpcResponse
        {
            Account = (int)p.Account,
            Role = p.Account.ToString(),
            Name = names[p.Account],
            AmountMsat = p.AmountMsat
        }).ToList(),
        Note = entry.Note
    };
}