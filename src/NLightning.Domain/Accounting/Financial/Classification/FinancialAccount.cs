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

    /// <summary>The cost-basis adjustment of the assets (A3-T4): the financial book values every msat line at the
    /// market price of its time, and this fiat-only asset line moves the assets from that value to the cost of the lots
    /// (a realized gain or loss, an imported basis), so the assets' fiat total is the open lots' cost.</summary>
    CostBasis = 5,

    // Income
    Sales = 10,
    Routing = 11,
    OtherIncome = 12,
    IncomeUnclassified = 13,
    RealizedGains = 14,

    /// <summary>The fees we earned selling liquidity (liquidity ads, NL-771).</summary>
    Liquidity = 15,

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

    /// <summary>The fees we paid buying liquidity (liquidity ads, NL-771).</summary>
    FeeLiquidity = 38,

    // Equity (the opening balances and the transfers default to the operational names)
    Opening = 50,
    TransfersIn = 51,
    TransfersOut = 52,

    /// <summary>Both halves of a rebalance (a self-payment): a transfer between our own channels, not a sale nor an
    /// expense; only its route fee disposes (D-A12).</summary>
    Rebalance = 53
}