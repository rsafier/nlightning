namespace NLightning.Domain.Accounting.Books;

using Models;

/// <summary>
/// The operational posting rules (plan <c>docs/agents/ACCOUNTING_PLAN.md</c> §6.1 "Rules"): one sealed event in, the
/// postings of its entry out. Pure; the result always sums to zero.
/// </summary>
public static class AccountingPostingRules
{
    /// <summary>
    /// The postings of <paramref name="accountingEvent"/>. <paramref name="findEntry"/> returns the entry of an earlier
    /// event key (for a <see cref="Enums.AccountingEventKind.Reversal"/>), or null.
    /// </summary>
    /// <exception cref="InvalidOperationException">The rules would not balance (a bug).</exception>
    public static IReadOnlyList<AccountingPosting> Post(AccountingEventModel accountingEvent,
                                                        Func<string, AccountingEntry?> findEntry)
    {
        throw new NotImplementedException("A2 lane B1");
    }
}
