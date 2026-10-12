namespace NLightning.Domain.Bitcoin.Wallet.Models;

using Money;
using ValueObjects;

/// <summary>
/// Wallet outputs reserved to pay a fee (a CPFP child, an anchor HTLC transaction; BOLT 5 plan O7-T1). The reservation
/// is persisted with its inputs, so a restart never hands the same outputs to another spend; it lasts until
/// <c>IFeeInputSelector.ReleaseAsync</c> (the spend was abandoned or replaced) or <c>ConfirmAsync</c> (it confirmed).
/// </summary>
public sealed class FeeInputReservation
{
    /// <summary>The reservation's id.</summary>
    public Guid Id { get; }

    /// <summary>What the inputs pay for (e.g. <c>cpfp:&lt;txid&gt;</c>); for logs and for finding it after a restart.</summary>
    public string Purpose { get; }

    /// <summary>The reserved wallet outputs, largest first.</summary>
    public IReadOnlyList<WalletInput> Inputs { get; }

    /// <summary>The sum of <see cref="Inputs"/>.</summary>
    public LightningMoney Total { get; }

    /// <summary>
    /// The fee the reservation was sized for: the requested target fee plus the requested fee rate over the extra weight,
    /// the inputs and, when <see cref="ChangeScript"/> is set, the change output. Without change it is the whole excess
    /// (<see cref="Total"/>), since a change below the dust limit is left to the fee.
    /// </summary>
    public LightningMoney Fee { get; }

    /// <summary>What goes back to the wallet (<see cref="Total"/> minus <see cref="Fee"/>); zero without change.</summary>
    public LightningMoney ChangeAmount { get; }

    /// <summary>A P2WPKH change script of the wallet, or null when no change output is needed.</summary>
    public BitcoinScript? ChangeScript { get; }

    public FeeInputReservation(Guid id, string purpose, IReadOnlyList<WalletInput> inputs, LightningMoney fee,
                               LightningMoney changeAmount, BitcoinScript? changeScript)
    {
        ArgumentNullException.ThrowIfNull(purpose);
        ArgumentNullException.ThrowIfNull(inputs);

        Id = id;
        Purpose = purpose;
        Inputs = inputs;
        Total = LightningMoney.Satoshis(inputs.Sum(i => i.Amount.Satoshi));
        Fee = fee;
        ChangeAmount = changeAmount;
        ChangeScript = changeScript;
    }
}