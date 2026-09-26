namespace NLightning.Domain.Bitcoin.Transactions.Factories;

using Bitcoin.Enums;
using Channels.Models;
using Constants;
using Exceptions;
using Interfaces;
using Models;
using Money;
using Wallet.Models;

public class FundingTransactionModelFactory : IFundingTransactionModelFactory
{
    public FundingTransactionModel Create(ChannelModel channel, List<UtxoModel> utxos,
                                          WalletAddressModel? changeAddress)
    {
        if (utxos.Count == 0)
            throw new ArgumentException("UTXO list cannot be empty", nameof(utxos));

        var fundingOutput = channel.FundingOutput ??
                            throw new NullReferenceException($"{nameof(channel.FundingOutput)} cannot be null");

        // Calculate the total input amount
        var totalInputAmount = LightningMoney.Satoshis(utxos.Sum(u => u.Amount.Satoshi));

        // Calculate the weight based on the input types
        // Starting with base transaction weight
        var weight = WeightConstants.TransactionBaseWeight;

        // Add weight for each input (assuming P2WPKH for now, which is most common)
        foreach (var utxo in utxos)
        {
            if (utxo.AddressType == AddressType.P2Wpkh)
            {
                weight += WeightConstants.P2WpkhInputWeight * 4
                        + WeightConstants.SingleSigWitnessWeight;
            }
            else if (utxo.AddressType == AddressType.P2Tr)
            {
                weight += WeightConstants.P2TrInputWeight * 4
                        + WeightConstants.TaprootSigWitnessWeight;
            }
            else
            {
                throw new NotSupportedException($"Unsupported utxo type {utxo.AddressType}");
            }
        }

        // Add weight for the funding output (P2WSH)
        weight += WeightConstants.P2WshOutputWeight;

        var feeRatePerKw = channel.ChannelParams.FeeRateAmountPerKw;
        var fundingAmount = fundingOutput.Amount;

        // Without a change output we need at least the funding amount plus the fee
        var feeWithoutChange = CalculateFee(weight, feeRatePerKw);
        var requiredAmount = fundingAmount + feeWithoutChange;
        if (totalInputAmount < requiredAmount)
            throw new InsufficientFundsException(requiredAmount, totalInputAmount);

        // Only add a change output if what is left after paying for it is not dust; otherwise the leftover goes to fee
        var isTaprootChange = changeAddress?.AddressType == AddressType.P2Tr;
        var changeDustLimit = isTaprootChange
                                  ? TransactionConstants.P2TrDustLimit
                                  : TransactionConstants.P2WpkhDustLimit;
        var changeOutputWeight = isTaprootChange
                                     ? WeightConstants.P2TrOutputWeight
                                     : WeightConstants.P2WpkhOutputWeight;
        var feeWithChange = CalculateFee(weight + changeOutputWeight, feeRatePerKw);
        var amountForChangeAndFee = totalInputAmount - fundingAmount;
        if (amountForChangeAndFee < feeWithChange + changeDustLimit)
            return new FundingTransactionModel(utxos, fundingOutput, amountForChangeAndFee);

        // Create the funding transaction model with a change output
        return new FundingTransactionModel(utxos, fundingOutput, feeWithChange)
        {
            ChangeAmount = amountForChangeAndFee - feeWithChange,
            ChangeAddress = changeAddress ??
                            throw new ArgumentNullException(nameof(changeAddress),
                                                            "We need a change address but none was provided.")
        };
    }

    /// <summary>
    /// Calculates the fee for <paramref name="weight"/> at <paramref name="feeRatePerKw"/>, rounded up to a whole
    /// satoshi (on-chain fees can't carry millisatoshis).
    /// </summary>
    private static LightningMoney CalculateFee(int weight, LightningMoney feeRatePerKw)
    {
        // weight * sat/kw = msat
        var feeMsat = (ulong)weight * (ulong)feeRatePerKw.Satoshi;
        return LightningMoney.Satoshis((feeMsat + 999) / 1_000);
    }
}