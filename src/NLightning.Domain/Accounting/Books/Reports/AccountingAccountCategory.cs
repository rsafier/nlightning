namespace NLightning.Domain.Accounting.Books.Reports;

/// <summary>
/// The top-level kind of an account (the root of its default name, plan §6.1).
/// </summary>
public enum AccountingAccountCategory
{
    Assets = 1,
    Liabilities = 2,
    Equity = 3,
    Income = 4,
    Expenses = 5
}

/// <summary>
/// The category of each <see cref="AccountRole"/>, from its default name (an operator's name override never moves an
/// account to another category).
/// </summary>
public static class AccountingAccountCategories
{
    /// <summary>The fee expense accounts: the route fees of our payments and the on-chain fees by purpose.</summary>
    public static IReadOnlyList<AccountRole> FeeAccounts { get; } =
    [
        AccountRole.RoutingFees, AccountRole.FeeFunding, AccountRole.FeeSplice, AccountRole.FeeClose,
        AccountRole.FeeCommitment, AccountRole.FeeSweep, AccountRole.FeeCpfp, AccountRole.FeeWithdraw
    ];

    /// <summary>
    /// The category of <paramref name="role"/>; a role this build does not know (stored by a newer one) is
    /// <see cref="AccountingAccountCategory.Equity"/>, so the balance sheet still balances.
    /// </summary>
    public static AccountingAccountCategory Of(AccountRole role) => AccountNames.Default.KindOf(role) switch
    {
        "assets" => AccountingAccountCategory.Assets,
        "liabilities" => AccountingAccountCategory.Liabilities,
        "income" => AccountingAccountCategory.Income,
        "expenses" => AccountingAccountCategory.Expenses,
        _ => AccountingAccountCategory.Equity
    };
}