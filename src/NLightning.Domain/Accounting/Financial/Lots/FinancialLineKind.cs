namespace NLightning.Domain.Accounting.Financial.Lots;

using Books;

/// <summary>What a line of a financial entry does to the cost-basis lots (D-A12; NL-602 A3-T4).</summary>
public enum FinancialLineKind
{
    /// <summary>No amount: nothing.</summary>
    None = 0,

    /// <summary>One of our own buckets (channels, pending, wallet, clearing): value moves between them, no lot
    /// changes.</summary>
    Asset = 1,

    /// <summary>A transfer between our own buckets through equity (a rebalance's halves): no lot changes.</summary>
    Transfer = 2,

    /// <summary>Sats we acquired (income at fair value, a deposit from outside, an opening balance, an expense taken
    /// back): a lot opens.</summary>
    Acquisition = 3,

    /// <summary>Sats we gave away (a payment, a fee, a withdrawal, a push, a loss, income taken back): lots are relieved
    /// and the gain or loss is realized.</summary>
    Disposal = 4
}

/// <summary>
/// Which lines of a financial entry acquire and dispose of sats (D-A12): everything between our own wallet, channels,
/// pending and clearing buckets (and a rebalance's equity transfer) is a transfer; an income, a deposit from outside or
/// an opening balance credited is an acquisition; an expense, a withdrawal or a push debited is a disposal (a loss at
/// zero proceeds). A line the other way round (a reversal) is the opposite.
/// </summary>
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

    /// <summary>What the line does to the lots.</summary>
    public static FinancialLineKind KindOf(AccountingPosting posting)
    {
        ArgumentNullException.ThrowIfNull(posting);
        if (posting.AmountMsat == 0)
            return FinancialLineKind.None;

        if (IsAsset(posting.Account))
            return FinancialLineKind.Asset;

        if (posting.Account == AccountRole.Rebalance)
            return FinancialLineKind.Transfer;

        // Income and equity coming in are credits when we acquire; expenses and equity going out are debits when we
        // dispose. The opposite sign is a reversal of such a line.
        var inward = posting.Account is AccountRole.Received or AccountRole.Routing or AccountRole.PushReceived
                                     or AccountRole.OnchainGain or AccountRole.TransfersIn or AccountRole.Opening;
        if (inward)
            return posting.AmountMsat < 0 ? FinancialLineKind.Acquisition : FinancialLineKind.Disposal;

        return posting.AmountMsat > 0 ? FinancialLineKind.Disposal : FinancialLineKind.Acquisition;
    }
}