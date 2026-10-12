namespace NLightning.Domain.Tests.Bitcoin.Transactions;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Money;

public class FundingFeeEstimatorTests
{
    private static readonly LightningMoney s_feeRate = LightningMoney.Satoshis(253);

    [Theory]
    [InlineData(1, 253, 166)] // 40 + 271 + 172 + 172 = 655 WU
    [InlineData(2, 253, 235)] // 926 WU
    [InlineData(1, 10_000, 6_550)]
    public void Given_AnInputCount_When_TheWorstCaseFeeIsEstimated_Then_ItPaysP2WpkhInputsAndAP2TrChange(
        int inputs, long feeRatePerKw, long expectedSat)
    {
        // Act
        var fee = FundingFeeEstimator.EstimateWorstCaseFee(inputs, LightningMoney.Satoshis(feeRatePerKw));

        // Assert
        Assert.Equal(expectedSat, fee.Satoshi);
    }

    [Fact]
    public void Given_ChangeAboveDust_When_TheMinimumChangeIsComputed_Then_ItIsTheInputsMinusFundingAndFee()
    {
        // Arrange
        UtxoModel[] inputs = [CreateUtxo(60_000, 100, AddressType.P2Wpkh)];

        // Act
        var change = FundingFeeEstimator.GetMinimumChange(inputs, LightningMoney.Satoshis(59_000), s_feeRate);

        // Assert
        Assert.Equal(60_000 - 59_000 - 166, change.Satoshi);
    }

    [Fact]
    public void Given_ChangeBelowTheP2TrDustLimit_When_TheMinimumChangeIsComputed_Then_ItIsZero()
    {
        // Arrange: 234 sat would be left, below 330 (the factory gives it to the fee)
        UtxoModel[] inputs = [CreateUtxo(60_000, 100, AddressType.P2Wpkh)];

        // Act
        var change = FundingFeeEstimator.GetMinimumChange(inputs, LightningMoney.Satoshis(59_600), s_feeRate);

        // Assert
        Assert.True(change.IsZero);
    }

    [Theory]
    [InlineData(97u, AddressType.P2Wpkh, true, true)]
    [InlineData(98u, AddressType.P2Wpkh, true, false)] // two confirmations only
    [InlineData(0u, AddressType.P2Wpkh, true, false)] // not mined
    [InlineData(97u, AddressType.P2Tr, true, true)]
    [InlineData(97u, AddressType.P2Wpkh, false, false)] // no known address
    public void Given_AWalletOutput_When_CheckedAt100_Then_ItBacksTheAnchorsReserveOnlyWhenTheFeeSelectorCanUseIt(
        uint blockHeight, AddressType type, bool withAddress, bool expected)
    {
        // Arrange
        var utxo = withAddress
                       ? CreateUtxo(10_000, blockHeight, type)
                       : new UtxoModel(new TxId(new byte[32]), 0, LightningMoney.Satoshis(10_000), blockHeight, 0, false,
                                       type);

        // Act / Assert
        Assert.Equal(expected, utxo.BacksAnchorReserve(100));
    }

    private static UtxoModel CreateUtxo(long amountSat, uint blockHeight, AddressType type) =>
        new(new TxId(new byte[32]), 0, LightningMoney.Satoshis(amountSat), blockHeight,
            new WalletAddressModel(type, 0, false, "address"));
}