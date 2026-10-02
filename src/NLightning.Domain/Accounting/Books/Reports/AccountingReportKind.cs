namespace NLightning.Domain.Accounting.Books.Reports;

/// <summary>
/// The reports of the operational books (plan <c>docs/agents/ACCOUNTING_PLAN.md</c> §6.1 "Reports", IPC 43). The values
/// go on the wire: never renumber them.
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
    Register = 6
}