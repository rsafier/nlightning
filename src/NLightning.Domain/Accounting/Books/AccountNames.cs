namespace NLightning.Domain.Accounting.Books;

/// <summary>
/// The hledger-style names of the accounts (plan §6.1), with optional overrides per role.
/// </summary>
public sealed class AccountNames
{
    private static readonly IReadOnlyDictionary<AccountRole, string> s_defaults = new Dictionary<AccountRole, string>
    {
        [AccountRole.Channels] = "assets:lightning:channels",
        [AccountRole.Pending] = "assets:onchain:pending",
        [AccountRole.Wallet] = "assets:onchain:wallet",
        [AccountRole.Clearing] = "assets:onchain:clearing",
        [AccountRole.Received] = "income:lightning:received",
        [AccountRole.Routing] = "income:lightning:routing",
        [AccountRole.PushReceived] = "income:lightning:push",
        [AccountRole.OnchainGain] = "income:onchain:gain",
        [AccountRole.Sent] = "expenses:lightning:sent",
        [AccountRole.RoutingFees] = "expenses:lightning:routing-fees",
        [AccountRole.Rebalance] = "expenses:lightning:rebalance",
        [AccountRole.PushSent] = "expenses:lightning:push",
        [AccountRole.FeeFunding] = "expenses:onchain:fees:funding",
        [AccountRole.FeeSplice] = "expenses:onchain:fees:splice",
        [AccountRole.FeeClose] = "expenses:onchain:fees:close",
        [AccountRole.FeeCommitment] = "expenses:onchain:fees:commitment",
        [AccountRole.FeeSweep] = "expenses:onchain:fees:sweep",
        [AccountRole.FeeCpfp] = "expenses:onchain:fees:cpfp",
        [AccountRole.FeeWithdraw] = "expenses:onchain:fees:withdraw",
        [AccountRole.LossOnchain] = "expenses:losses:onchain",
        [AccountRole.TransfersIn] = "equity:transfers:in",
        [AccountRole.TransfersOut] = "equity:transfers:out",
        [AccountRole.Opening] = "equity:opening-balances"
    };

    private readonly IReadOnlyDictionary<AccountRole, string> _names;

    public AccountNames(IReadOnlyDictionary<AccountRole, string>? overrides = null)
    {
        var names = new Dictionary<AccountRole, string>(s_defaults);
        if (overrides is not null)
            foreach (var (role, name) in overrides)
                if (!string.IsNullOrWhiteSpace(name))
                    names[role] = name.Trim();
        _names = names;
    }

    public static AccountNames Default { get; } = new();

    public string this[AccountRole role] => _names.TryGetValue(role, out var name) ? name : role.ToString();

    /// <summary>The top-level kind of an account's name (assets, income, expenses, equity).</summary>
    public string KindOf(AccountRole role) => this[role].Split(':')[0];
}
