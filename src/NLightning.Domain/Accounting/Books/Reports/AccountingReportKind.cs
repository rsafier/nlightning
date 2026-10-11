namespace NLightning.Domain.Accounting.Books.Reports;

/// <summary>
/// The reports of the books (plan <c>docs/agents/ACCOUNTING_PLAN.md</c> §6.1 "Reports", IPC 43; 7 to 12 are the financial
/// book's, §6.2, A3-T6). The values go on the wire: never renumber them.
/// </summary>
public enum AccountingReportKind
{
    /// <summary>Account balances at a time: assets against equity and the period's earnings.</summary>
    BalanceSheet = 1,

    /// <summary>Income and expenses of a period.</summary>
    IncomeStatement = 2,

    /// <summary>Per channel: routing earned, payments, rebalance cost, on-chain fees, capacity and yield.</summary>
    Channels = 3,

    /// <summary>The channel view summed per peer.</summary>
    Peers = 4,

    /// <summary>Every fee expense account, plus the confirmed sweep fee bumps.</summary>
    Fees = 5,

    /// <summary>The entries with their postings in ledger order.</summary>
    Register = 6,

    /// <summary>The financial book's realized gains by period (NL-602 A3-T6).</summary>
    RealizedGains = 7,

    /// <summary>The open lots with their market value and unrealized gain at a price (A3-T6).</summary>
    UnrealizedGains = 8,

    /// <summary>The open cost-basis lots (A3-T6).</summary>
    Lots = 9,

    /// <summary>The financial book's postings without a fiat value yet (A3-T6).</summary>
    Unvalued = 10,

    /// <summary>The financial book's entries that went to an unclassified account (A3-T6).</summary>
    Unclassified = 11,

    /// <summary>The risk-weighted capital of a live snapshot (A3-T6, plan §6.2 "Audit").</summary>
    RiskCapital = 12
}