namespace NLightning.Domain.Bitcoin.Wallet.Models;

using Accounting.Labels;
using Money;
using ValueObjects;

/// <summary>
/// An on-chain payment from the wallet to an external address (<c>withdraw</c>).
/// </summary>
/// <param name="Address">The destination address; it must be of the node's network.</param>
/// <param name="Amount">What the destination receives, or null to send everything the wallet may spend ("all"): the
/// confirmed outputs, minus the fee and minus the anchors reserve, which goes back to the wallet as change.</param>
/// <param name="FeeRatePerKw">The fee rate in sat per 1000 weight units, or null for the fee service's estimate.</param>
public sealed record WalletWithdrawRequest(string Address, LightningMoney? Amount, LightningMoney? FeeRatePerKw)
{
    /// <summary>True when the request sends everything the wallet may spend.</summary>
    public bool SendAll => Amount is null;

    /// <summary>
    /// The operator's label and tags (NL-602 A3-T1) the <c>WalletSend</c> broadcast row (and its <c>WalletSent</c>
    /// event) carries; <see cref="SourceLabels.None"/> for none.
    /// </summary>
    public SourceLabels Labels { get; init; } = SourceLabels.None;

    /// <summary>
    /// The highest fee the transaction may pay, or null for no limit: a signed transaction paying more is dropped
    /// before it is stored or broadcast (<see cref="Enums.WalletSpendError.FeeAboveLimit"/>; NL-997).
    /// </summary>
    public LightningMoney? MaxFee { get; init; }

    /// <summary>
    /// The exact wallet outputs to spend (NL-1296, <c>withdraw --utxo</c>), or null to let the coin selector choose.
    /// Every listed output is spent, silent payment coins included (the explicit opt-in that
    /// <c>SilentPayments:AvoidMixing</c> otherwise keeps them out of a spend that does not need them); "all" sends
    /// their whole value minus the fee, with no change, and the anchors reserve must stay backed by the other outputs.
    /// </summary>
    public IReadOnlyList<(TxId TxId, uint Index)>? Inputs { get; init; }
}