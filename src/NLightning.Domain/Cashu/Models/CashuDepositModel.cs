namespace NLightning.Domain.Cashu.Models;

using Bitcoin.ValueObjects;
using Money;

/// <summary>
/// One output paid to an on-chain mint quote's address (NL-997, table <c>CashuDeposits</c>): CDK's <c>payment_id</c>
/// is its outpoint, one per output (NUT-30). Kept after the wallet spends the output.
/// </summary>
/// <param name="QuoteId">The mint quote whose address it pays.</param>
/// <param name="TxId">The deposit transaction.</param>
/// <param name="OutputIndex">The output paying the quote's address.</param>
/// <param name="Amount">The output's value.</param>
/// <param name="BlockHeight">The block that confirmed it.</param>
public sealed record CashuDepositModel(string QuoteId, TxId TxId, uint OutputIndex, LightningMoney Amount,
                                       uint BlockHeight)
{
    /// <summary>When the processor told the mint (the deposit had its confirmations); null until then.</summary>
    public DateTimeOffset? ReportedAt { get; set; }

    /// <summary>CDK's <c>payment_id</c>: <c>txid:vout</c>.</summary>
    public string Outpoint => $"{TxId}:{OutputIndex}";

    /// <summary>The deposit's confirmations at <paramref name="tipHeight"/> (0 above the tip).</summary>
    public uint ConfirmationsAt(uint tipHeight) => tipHeight >= BlockHeight ? tipHeight - BlockHeight + 1 : 0;
}