namespace NLightning.Domain.Bitcoin.Transactions.Factories;

using Bitcoin.Enums;
using Constants;
using Money;
using Wallet.Models;

/// <summary>
/// The fee arithmetic of <see cref="FundingTransactionModelFactory"/>, shared with the channel funding coin selection
/// and the anchors reserve checks (NL-379), so the reserve left in the wallet is what the funding transaction really
/// leaves: the inputs minus the funding amount minus the fee, and nothing when the change would be dust.
/// </summary>
public static class FundingFeeEstimator
{
    /// <summary>The heaviest change output (P2TR).</summary>
    public const int WorstCaseChangeOutputWeight = WeightConstants.P2TrOutputWeight;

    /// <summary>The heaviest input a funding transaction takes (P2WPKH), for an estimate before the inputs are known.</summary>
    public static int WorstCaseInputWeight => GetInputWeight(AddressType.P2Wpkh);

    /// <summary>The weight of a funding transaction input of <paramref name="addressType"/>, witness included.</summary>
    public static int GetInputWeight(AddressType addressType) => addressType switch
    {
        AddressType.P2Wpkh => WeightConstants.P2WpkhInputWeight * 4 + WeightConstants.SingleSigWitnessWeight,
        AddressType.P2Tr => WeightConstants.P2TrInputWeight * 4 + WeightConstants.TaprootSigWitnessWeight,
        _ => throw new NotSupportedException($"Unsupported utxo type {addressType}")
    };

    /// <summary>Whether a wallet output can fund a channel (the input types the factory supports).</summary>
    public static bool CanFund(UtxoModel utxo) => utxo.AddressType is AddressType.P2Wpkh or AddressType.P2Tr;

    /// <summary>
    /// The weight of a funding transaction whose inputs weigh <paramref name="inputWeight"/> in total, with the P2WSH
    /// funding output and, when <paramref name="changeOutputWeight"/> is not zero, a change output of that weight.
    /// </summary>
    public static int GetWeight(int inputWeight, int changeOutputWeight) =>
        WeightConstants.TransactionBaseWeight + inputWeight + WeightConstants.P2WshOutputWeight + changeOutputWeight;

    /// <summary>
    /// The fee for <paramref name="weight"/> at <paramref name="feeRatePerKw"/>, rounded up to a whole satoshi (on-chain
    /// fees can't carry millisatoshis).
    /// </summary>
    public static LightningMoney CalculateFee(int weight, LightningMoney feeRatePerKw)
    {
        ArgumentNullException.ThrowIfNull(feeRatePerKw);

        // weight * sat/kw = msat
        var feeMsat = (ulong)weight * (ulong)feeRatePerKw.Satoshi;
        return LightningMoney.Satoshis((feeMsat + 999) / 1_000);
    }

    /// <summary>
    /// A fee no funding transaction with <paramref name="inputCount"/> inputs pays more than: every input P2WPKH and a
    /// P2TR change output.
    /// </summary>
    public static LightningMoney EstimateWorstCaseFee(int inputCount, LightningMoney feeRatePerKw)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputCount);
        return CalculateFee(GetWeight(inputCount * WorstCaseInputWeight, WorstCaseChangeOutputWeight), feeRatePerKw);
    }

    /// <summary>
    /// The least change a funding of <paramref name="fundingAmount"/> from <paramref name="inputs"/> returns to the
    /// wallet, whatever the change address type: the inputs minus the funding minus the fee with a P2TR change output,
    /// or zero when that is below the P2TR dust limit (the factory then gives the leftover to the fee).
    /// </summary>
    public static LightningMoney GetMinimumChange(IReadOnlyCollection<UtxoModel> inputs, LightningMoney fundingAmount,
                                                  LightningMoney feeRatePerKw)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(fundingAmount);

        var total = LightningMoney.Satoshis(inputs.Sum(u => u.Amount.Satoshi));
        var fee = CalculateFee(GetWeight(inputs.Sum(u => GetInputWeight(u.AddressType)), WorstCaseChangeOutputWeight),
                               feeRatePerKw);
        if (total < fundingAmount + fee + TransactionConstants.P2TrDustLimit)
            return LightningMoney.Zero;

        return total - fundingAmount - fee;
    }
}