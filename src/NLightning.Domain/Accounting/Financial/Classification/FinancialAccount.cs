namespace NLightning.Domain.Accounting.Financial.Classification;

/// <summary>
/// The accounts of the financial chart (NL-602 A3-T3, plan <c>docs/agents/ACCOUNTING_PLAN.md</c> §9 A3-T3). Their names
/// are configurable (<c>Accounting:FinancialAccountNames:&lt;Account&gt;</c>, <see cref="FinancialChart"/>); the
/// financial book stores the name of each line, never these values, so they may be renumbered, but keep them stable for
/// the configuration keys.
/// </summary>
public enum FinancialAccount
{
    // Assets: the operational buckets (their names default to the operational ones)
    Channels = 1,
    Pending = 2,
    Wallet = 3,
    Clearing = 4,

    // Income
    Sales = 10,
    Routing = 11,
    OtherIncome = 12,
    IncomeUnclassified = 13,
    RealizedGains = 14,

    // Expenses
    Payments = 20,
    Losses = 21,
    ExpensesUnclassified = 22,
    RealizedLosses = 23,

    // Fees, one per operational fee role
    FeeRouting = 30,
    FeeFunding = 31,
    FeeSplice = 32,
    FeeClose = 33,
    FeeCommitment = 34,
    FeeSweep = 35,
    FeeCpfp = 36,
    FeeWithdraw = 37,

    // Equity (the opening balances and the transfers default to the operational names)
    Opening = 50,
    TransfersIn = 51,
    TransfersOut = 52,

    /// <summary>Both halves of a rebalance (a self-payment): a transfer between our own channels, not a sale nor an
    /// expense; only its route fee disposes (D-A12).</summary>
    Rebalance = 53
}