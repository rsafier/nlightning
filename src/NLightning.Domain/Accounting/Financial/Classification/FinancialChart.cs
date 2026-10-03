namespace NLightning.Domain.Accounting.Financial.Classification;

using Books;

/// <summary>
/// The financial chart of accounts (NL-602 A3-T3, plan <c>docs/agents/ACCOUNTING_PLAN.md</c> §9 A3-T3): the names of
/// the <see cref="FinancialAccount"/>s, configurable like the operational <see cref="AccountNames"/>
/// (<c>Accounting:FinancialAccountNames:&lt;Account&gt;</c>), and the default account of every operational line.
/// </summary>
/// <remarks>
/// <para>Defaults: the assets, the opening balances and the transfers keep the operational names in effect (so a
/// renamed operational bucket is renamed here too); <c>income:sales</c> (received payments), <c>income:routing</c>,
/// <c>income:liquidity</c> (liquidity sold, NL-771), <c>income:other</c> (on-chain gains), <c>income:unclassified</c>,
/// <c>income:gains:realized</c>, <c>expenses:payments</c> (sent payments), <c>expenses:losses</c>,
/// <c>expenses:unclassified</c>, <c>expenses:losses:realized</c>,
/// <c>expenses:fees:{routing,liquidity,funding,splice,close,commitment,sweep,cpfp,withdraw}</c>
/// and <c>equity:transfers:rebalance</c> (both halves of a self-payment, D-A12), and <c>assets:cost-basis</c> (the
/// financial projector's fiat-only adjustment from the market value of the asset lines to the lots' cost, A3-T4).</para>
/// <para>Only the lines of the <see cref="IsClassifiable">classifiable roles</see> (the income and expense lines that
/// say what the money was for, and the transfers in and out of the wallet) follow the classification; fees, assets and
/// the opening balances always go to their chart account. The pushes have no meaning the node can tell, so their
/// default is the unclassified account of their side and they are listed for review.</para>
/// </remarks>
public sealed class FinancialChart
{
    private static readonly IReadOnlyDictionary<FinancialAccount, string> s_fixedDefaults =
        new Dictionary<FinancialAccount, string>
        {
            [FinancialAccount.Sales] = "income:sales",
            [FinancialAccount.Routing] = "income:routing",
            [FinancialAccount.OtherIncome] = "income:other",
            [FinancialAccount.IncomeUnclassified] = "income:unclassified",
            [FinancialAccount.RealizedGains] = "income:gains:realized",
            [FinancialAccount.Liquidity] = "income:liquidity",
            [FinancialAccount.Payments] = "expenses:payments",
            [FinancialAccount.Losses] = "expenses:losses",
            [FinancialAccount.ExpensesUnclassified] = "expenses:unclassified",
            [FinancialAccount.RealizedLosses] = "expenses:losses:realized",
            [FinancialAccount.FeeRouting] = "expenses:fees:routing",
            [FinancialAccount.FeeFunding] = "expenses:fees:funding",
            [FinancialAccount.FeeSplice] = "expenses:fees:splice",
            [FinancialAccount.FeeClose] = "expenses:fees:close",
            [FinancialAccount.FeeCommitment] = "expenses:fees:commitment",
            [FinancialAccount.FeeSweep] = "expenses:fees:sweep",
            [FinancialAccount.FeeCpfp] = "expenses:fees:cpfp",
            [FinancialAccount.FeeWithdraw] = "expenses:fees:withdraw",
            [FinancialAccount.FeeLiquidity] = "expenses:fees:liquidity",
            [FinancialAccount.Rebalance] = "equity:transfers:rebalance",
            [FinancialAccount.CostBasis] = "assets:cost-basis"
        };

    // The financial accounts whose default name is the operational one of a role
    private static readonly IReadOnlyDictionary<FinancialAccount, AccountRole> s_operationalNamed =
        new Dictionary<FinancialAccount, AccountRole>
        {
            [FinancialAccount.Channels] = AccountRole.Channels,
            [FinancialAccount.Pending] = AccountRole.Pending,
            [FinancialAccount.Wallet] = AccountRole.Wallet,
            [FinancialAccount.Clearing] = AccountRole.Clearing,
            [FinancialAccount.Opening] = AccountRole.Opening,
            [FinancialAccount.TransfersIn] = AccountRole.TransfersIn,
            [FinancialAccount.TransfersOut] = AccountRole.TransfersOut
        };

    private static readonly IReadOnlyDictionary<AccountRole, FinancialAccount> s_roleDefaults =
        new Dictionary<AccountRole, FinancialAccount>
        {
            [AccountRole.Channels] = FinancialAccount.Channels,
            [AccountRole.Pending] = FinancialAccount.Pending,
            [AccountRole.Wallet] = FinancialAccount.Wallet,
            [AccountRole.Clearing] = FinancialAccount.Clearing,
            [AccountRole.Received] = FinancialAccount.Sales,
            [AccountRole.Routing] = FinancialAccount.Routing,
            [AccountRole.PushReceived] = FinancialAccount.IncomeUnclassified,
            [AccountRole.OnchainGain] = FinancialAccount.OtherIncome,
            [AccountRole.LiquidityIncome] = FinancialAccount.Liquidity,
            [AccountRole.Sent] = FinancialAccount.Payments,
            [AccountRole.RoutingFees] = FinancialAccount.FeeRouting,
            [AccountRole.Rebalance] = FinancialAccount.Rebalance,
            [AccountRole.PushSent] = FinancialAccount.ExpensesUnclassified,
            [AccountRole.LiquidityFees] = FinancialAccount.FeeLiquidity,
            [AccountRole.FeeFunding] = FinancialAccount.FeeFunding,
            [AccountRole.FeeSplice] = FinancialAccount.FeeSplice,
            [AccountRole.FeeClose] = FinancialAccount.FeeClose,
            [AccountRole.FeeCommitment] = FinancialAccount.FeeCommitment,
            [AccountRole.FeeSweep] = FinancialAccount.FeeSweep,
            [AccountRole.FeeCpfp] = FinancialAccount.FeeCpfp,
            [AccountRole.FeeWithdraw] = FinancialAccount.FeeWithdraw,
            [AccountRole.LossOnchain] = FinancialAccount.Losses,
            [AccountRole.TransfersIn] = FinancialAccount.TransfersIn,
            [AccountRole.TransfersOut] = FinancialAccount.TransfersOut,
            [AccountRole.Opening] = FinancialAccount.Opening
        };

    private static readonly HashSet<AccountRole> s_classifiable =
    [
        AccountRole.Received, AccountRole.Routing, AccountRole.PushReceived, AccountRole.OnchainGain,
        AccountRole.LiquidityIncome, AccountRole.Sent, AccountRole.Rebalance, AccountRole.PushSent, AccountRole.LossOnchain,
        AccountRole.TransfersIn, AccountRole.TransfersOut
    ];

    private readonly IReadOnlyDictionary<FinancialAccount, string> _names;

    /// <summary>
    /// The chart with <paramref name="operationalNames"/> (the operational names in effect, for the assets, the
    /// opening balances and the transfers) and <paramref name="overrides"/> by account. An override that is not a
    /// valid name (<see cref="FinancialAccountNameRules.TryValidate"/>) is ignored and listed in
    /// <see cref="IgnoredOverrides"/>, so a typo in the configuration never lands in the books.
    /// </summary>
    public FinancialChart(AccountNames? operationalNames = null,
                          IReadOnlyDictionary<FinancialAccount, string>? overrides = null)
    {
        var operational = operationalNames ?? AccountNames.Default;
        var names = new Dictionary<FinancialAccount, string>(s_fixedDefaults);
        foreach (var (account, role) in s_operationalNamed)
            names[account] = operational[role];

        var ignored = new List<string>();
        if (overrides is not null)
        {
            foreach (var (account, name) in overrides)
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var trimmed = name.Trim();
                if (!Enum.IsDefined(account))
                {
                    ignored.Add($"{(int)account}: not a financial account");
                    continue;
                }

                if (!FinancialAccountNameRules.TryValidate(trimmed, out var error))
                {
                    ignored.Add($"{account}: {error}");
                    continue;
                }

                names[account] = trimmed;
            }
        }

        _names = names;
        IgnoredOverrides = ignored;
    }

    /// <summary>The chart with the default names.</summary>
    public static FinancialChart Default { get; } = new();

    /// <summary>The name of <paramref name="account"/>.</summary>
    public string this[FinancialAccount account] => _names.TryGetValue(account, out var name) ? name : account.ToString();

    /// <summary>Every account's name.</summary>
    public IReadOnlyDictionary<FinancialAccount, string> Names => _names;

    /// <summary>The configured names that were not used (account and why).</summary>
    public IReadOnlyList<string> IgnoredOverrides { get; }

    /// <summary>
    /// Whether a line of <paramref name="role"/> follows the classification (a rule or an override); the other lines
    /// always go to their chart account.
    /// </summary>
    public static bool IsClassifiable(AccountRole role) => s_classifiable.Contains(role);

    /// <summary>
    /// The chart account of an operational line of <paramref name="role"/> when no rule or override classifies it. A
    /// role this build does not know goes to the unclassified account of its category (equity: the opening balances).
    /// </summary>
    public static FinancialAccount DefaultAccountOf(AccountRole role)
    {
        if (s_roleDefaults.TryGetValue(role, out var account))
            return account;

        return Books.Reports.AccountingAccountCategories.Of(role) switch
        {
            Books.Reports.AccountingAccountCategory.Income => FinancialAccount.IncomeUnclassified,
            Books.Reports.AccountingAccountCategory.Expenses => FinancialAccount.ExpensesUnclassified,
            _ => FinancialAccount.Opening
        };
    }

    /// <summary>Whether <paramref name="accountName"/> is one of the two unclassified accounts.</summary>
    public bool IsUnclassified(string? accountName) =>
        accountName is not null
     && (string.Equals(accountName, this[FinancialAccount.IncomeUnclassified], StringComparison.Ordinal)
      || string.Equals(accountName, this[FinancialAccount.ExpensesUnclassified], StringComparison.Ordinal));
}