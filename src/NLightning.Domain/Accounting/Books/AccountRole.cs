namespace NLightning.Domain.Accounting.Books;

/// <summary>
/// The accounts of the operational books (plan <c>docs/agents/ACCOUNTING_PLAN.md</c> §6.1). The values are persisted:
/// never renumber them.
/// </summary>
public enum AccountRole
{
    Channels = 1,
    Pending = 2,
    Wallet = 3,
    Clearing = 4,
    Received = 10,
    Routing = 11,
    PushReceived = 12,
    OnchainGain = 13,

    /// <summary>The fees we earned selling liquidity (liquidity ads, NL-771).</summary>
    LiquidityIncome = 14,

    Sent = 20,
    RoutingFees = 21,
    Rebalance = 22,
    PushSent = 23,

    /// <summary>The fees we paid buying liquidity (liquidity ads, NL-771): mining and service fee.</summary>
    LiquidityFees = 24,

    FeeFunding = 30,
    FeeSplice = 31,
    FeeClose = 32,
    FeeCommitment = 33,
    FeeSweep = 34,
    FeeCpfp = 35,
    FeeWithdraw = 36,
    LossOnchain = 40,
    TransfersIn = 50,
    TransfersOut = 51,
    Opening = 52
}