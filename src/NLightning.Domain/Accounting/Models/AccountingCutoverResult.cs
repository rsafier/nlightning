namespace NLightning.Domain.Accounting.Models;

using Enums;

/// <summary>
/// What <see cref="Interfaces.IAccountingBackfill.EnsureCutoverAsync"/> did.
/// </summary>
/// <param name="Outcome">Whether the cutover was written now, already existed, or was written without opening
/// balances.</param>
/// <param name="CutoverAt">When the cutover was taken (null when it already existed).</param>
/// <param name="BlockHeight">The chain monitor's last processed height at the cutover (0 when unknown).</param>
/// <param name="ChannelCount">Opening balances of channels (bucket <c>channel:{id}</c>).</param>
/// <param name="ChannelMsat">Their sum (our gross local balances).</param>
/// <param name="PendingChannelCount">Opening balances of force closes being resolved (bucket <c>pending:{id}</c>).
/// </param>
/// <param name="PendingMsat">Their sum.</param>
/// <param name="WalletUtxoCount">Wallet outputs in the wallet's opening balance.</param>
/// <param name="WalletMsat">The wallet's opening balance.</param>
/// <param name="AwaitingFundingCount">Channels still waiting for their funding confirmation (no opening balance: their
/// <c>ChannelFunded</c> event comes later).</param>
public sealed record AccountingCutoverResult(
    AccountingCutoverOutcome Outcome,
    DateTimeOffset? CutoverAt = null,
    uint BlockHeight = 0,
    int ChannelCount = 0,
    long ChannelMsat = 0,
    int PendingChannelCount = 0,
    long PendingMsat = 0,
    int WalletUtxoCount = 0,
    long WalletMsat = 0,
    int AwaitingFundingCount = 0)
{
    /// <summary>The marker existed: nothing was done.</summary>
    public static AccountingCutoverResult AlreadyDone { get; } = new(AccountingCutoverOutcome.AlreadyDone);
}