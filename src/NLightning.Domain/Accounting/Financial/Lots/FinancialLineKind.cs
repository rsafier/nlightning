namespace NLightning.Domain.Accounting.Financial.Lots;

using Books;
using Books.Reports;
using Classification;

/// <summary>What a line of a financial entry does to the cost-basis lots (D-A12; NL-602 A3-T4, NL-657, NL-674).</summary>
public enum FinancialLineKind
{
    /// <summary>No amount: nothing.</summary>
    None = 0,

    /// <summary>One of our own buckets (channels, pending, wallet, clearing): its lots move with the value, nothing is
    /// realized.</summary>
    Asset = 1,

    /// <summary>A rebalance's equity transfer (both halves of a self-payment): the lots move through the rebalance
    /// bucket (<see cref="AccountingLotBucket.Rebalance"/>), nothing is realized.</summary>
    Transfer = 2,

    /// <summary>Sats we acquired (income at fair value, a deposit from outside, an opening balance, an expense taken
    /// back): a lot opens.</summary>
    Acquisition = 3,

    /// <summary>Sats we gave away (a payment, a fee, a withdrawal, a push, a loss, income taken back): lots are relieved
    /// and the gain or loss is realized.</summary>
    Disposal = 4,

    /// <summary>
    /// A withdrawal or deposit classified to an equity transfer account (NL-674, D-A12 as amended 2026-10-02): the sats
    /// stay ours, held outside the node (<see cref="AccountingLotBucket.HeldOutside"/>); a withdrawal moves its lots
    /// there at cost and a deposit takes them back at their original cost and time; nothing is realized.
    /// </summary>
    HeldOutside = 5
}

/// <summary>
/// Which lines of a financial entry acquire and dispose of sats (D-A12): everything between our own wallet, channels,
/// pending and clearing buckets (a rebalance's equity transfer, a transfer to or from the sats held outside the node
/// included) is a transfer; an income, a deposit from outside or an opening balance credited is an acquisition; an
/// expense, a withdrawal or a push debited is a disposal (a loss at zero proceeds). A line the other way round (a
/// reversal) is the opposite.
/// </summary>
/// <remarks>
/// <para><b>The classified account decides</b> (NL-674): a withdrawal (<see cref="AccountRole.TransfersOut"/>) or a
/// deposit from outside (<see cref="AccountRole.TransfersIn"/>) whose line a rule or an override sent to an
/// <c>equity:*</c> account other than the chart's own transfer accounts (say <c>equity:transfers:cold-storage</c>) is a
/// <see cref="FinancialLineKind.HeldOutside"/> transfer; with the default <c>equity:transfers:out</c>/<c>in</c> (the
/// operator said nothing of where the sats went) or an income or expense account it stays a disposal or acquisition.
/// A rebalance line (<see cref="AccountRole.Rebalance"/>) sent to an <c>income:*</c> or <c>expenses:*</c> account
/// acquires or disposes; in any other account it is a transfer.</para>
/// </remarks>
public static class FinancialLotRules
{
    /// <summary>Whether <paramref name="role"/> is one of our own buckets.</summary>
    public static bool IsAsset(AccountRole role) =>
        role is AccountRole.Channels or AccountRole.Pending or AccountRole.Wallet or AccountRole.Clearing;

    /// <summary>
    /// Whether the line debits the opening balances (NL-673): only the reversal of a wallet fact from before the feed
    /// does that (the cutover counted sats that never arrived). Such a disposal is a correction of the cutover, not a
    /// sale: it relieves the opening (or imported) lots first at their cost and realizes no gain.
    /// </summary>
    public static bool IsOpeningCorrection(AccountingPosting posting)
    {
        ArgumentNullException.ThrowIfNull(posting);
        return posting.Account == AccountRole.Opening && posting.AmountMsat > 0;
    }

    /// <summary>Whether a disposal of <paramref name="role"/> fetches nothing (D-A12: losses, breach losses).</summary>
    public static bool HasZeroProceeds(AccountRole role) => role == AccountRole.LossOnchain;

    /// <summary>What the line does to the lots, with the default chart's names.</summary>
    public static FinancialLineKind KindOf(AccountingPosting posting) => KindOf(posting, FinancialChart.Default);

    /// <summary>What the line does to the lots under <paramref name="chart"/> (see the class remarks).</summary>
    public static FinancialLineKind KindOf(AccountingPosting posting, FinancialChart chart)
    {
        ArgumentNullException.ThrowIfNull(posting);
        ArgumentNullException.ThrowIfNull(chart);
        if (posting.AmountMsat == 0)
            return FinancialLineKind.None;

        if (IsAsset(posting.Account))
            return FinancialLineKind.Asset;

        var category = posting.AccountName is { Length: > 0 } name
                           ? FinancialAccountNameRules.CategoryOf(name)
                           : (AccountingAccountCategory?)null;
        if (posting.Account == AccountRole.Rebalance)
        {
            return category switch
            {
                AccountingAccountCategory.Income or AccountingAccountCategory.Expenses =>
                    posting.AmountMsat > 0 ? FinancialLineKind.Disposal : FinancialLineKind.Acquisition,
                _ => FinancialLineKind.Transfer
            };
        }

        if (IsHeldOutside(posting, chart))
            return FinancialLineKind.HeldOutside;

        // Income and equity coming in are credits when we acquire; expenses and equity going out are debits when we
        // dispose. The opposite sign is a reversal of such a line.
        var inward = posting.Account is AccountRole.Received or AccountRole.Routing or AccountRole.PushReceived
                                     or AccountRole.OnchainGain or AccountRole.LiquidityIncome
                                     or AccountRole.TransfersIn or AccountRole.Opening;
        if (inward)
            return posting.AmountMsat < 0 ? FinancialLineKind.Acquisition : FinancialLineKind.Disposal;

        return posting.AmountMsat > 0 ? FinancialLineKind.Disposal : FinancialLineKind.Acquisition;
    }

    /// <summary>
    /// Whether a withdrawal or deposit line was classified to an equity transfer account of the operator's (NL-674): a
    /// <see cref="AccountRole.TransfersOut"/> or <see cref="AccountRole.TransfersIn"/> line whose account is in the
    /// equity category and is neither of the chart's own transfer accounts.
    /// </summary>
    public static bool IsHeldOutside(AccountingPosting posting, FinancialChart chart)
    {
        ArgumentNullException.ThrowIfNull(posting);
        ArgumentNullException.ThrowIfNull(chart);
        if (posting.Account is not (AccountRole.TransfersOut or AccountRole.TransfersIn)
         || posting.AccountName is not { Length: > 0 } name
         || FinancialAccountNameRules.CategoryOf(name) != AccountingAccountCategory.Equity)
            return false;

        return !string.Equals(name, chart[FinancialAccount.TransfersOut], StringComparison.Ordinal)
            && !string.Equals(name, chart[FinancialAccount.TransfersIn], StringComparison.Ordinal);
    }

    /// <summary>The bucket a line of kind <see cref="FinancialLineKind.Asset"/>, <see cref="FinancialLineKind.Transfer"/>
    /// or <see cref="FinancialLineKind.HeldOutside"/> moves lots into or out of, or null for any other line.</summary>
    public static AccountingLotBucket? BucketOf(AccountingPosting posting, FinancialChart chart)
    {
        ArgumentNullException.ThrowIfNull(posting);
        return KindOf(posting, chart) switch
        {
            FinancialLineKind.Asset => (AccountingLotBucket)(int)posting.Account,
            FinancialLineKind.Transfer => AccountingLotBucket.Rebalance,
            FinancialLineKind.HeldOutside => AccountingLotBucket.HeldOutside,
            _ => null
        };
    }
}