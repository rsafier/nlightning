namespace NLightning.Domain.Bitcoin.Wallet.Models;

using Money;
using ValueObjects;

/// <summary>
/// A wallet spend that was signed and stored for broadcast (<c>withdraw</c>).
/// </summary>
/// <param name="TxId">The transaction (internal byte order).</param>
/// <param name="Amount">What the destination receives.</param>
/// <param name="Fee">The fee the transaction pays.</param>
/// <param name="Change">What goes back to the wallet (zero without a change output).</param>
/// <param name="FeeRatePerKw">The fee rate it was built for, in sat per 1000 weight units.</param>
/// <param name="Weight">The signed transaction's weight.</param>
/// <param name="InputCount">The wallet outputs it spends.</param>
/// <param name="AnchorReserve">The anchors reserve the wallet keeps (zero without anchors channels).</param>
/// <param name="Published">True when bitcoind accepted it; false when the send was refused (it stays stored and is sent
/// again after every block until it confirms).</param>
public sealed record WalletWithdrawResult(
    TxId TxId,
    LightningMoney Amount,
    LightningMoney Fee,
    LightningMoney Change,
    LightningMoney FeeRatePerKw,
    int Weight,
    int InputCount,
    LightningMoney AnchorReserve,
    bool Published)
{
    /// <summary>The output of <see cref="TxId"/> that pays the destination: always the first (the change follows).</summary>
    public uint DestinationOutputIndex => 0;
}