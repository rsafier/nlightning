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

        // The weight of the inputs (P2WPKH or P2TR), the P2WSH funding output and no change yet (shared with the funding
        // coin selection and the anchors reserve checks, NL-379)
        var inputWeight = utxos.Sum(u => FundingFeeEstimator.GetInputWeight(u.AddressType));
        var weight = FundingFeeEstimator.GetWeight(inputWeight, 0);

        var feeRatePerKw = channel.ChannelParams.FeeRateAmountPerKw;
        var fundingAmount = fundingOutput.Amount;

        // Without a change output we need at least the funding amount plus the fee
        var feeWithoutChange = FundingFeeEstimator.CalculateFee(weight, feeRatePerKw);
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
        var feeWithChange = FundingFeeEstimator.CalculateFee(weight + changeOutputWeight, feeRatePerKw);
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
}