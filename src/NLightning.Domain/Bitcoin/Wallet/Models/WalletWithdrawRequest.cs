namespace NLightning.Domain.Bitcoin.Wallet.Models;

using Money;

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
}