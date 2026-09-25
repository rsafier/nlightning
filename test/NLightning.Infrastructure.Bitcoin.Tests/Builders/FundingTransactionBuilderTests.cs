using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Infrastructure.Bitcoin.Tests.Builders;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.Wallet.Models;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Outputs;

public class FundingTransactionBuilderTests
{
    private static readonly LightningMoney s_fundingAmount = LightningMoney.Satoshis(100_000);
    private static readonly LightningMoney s_fee = LightningMoney.Satoshis(1_518);

    private readonly FundingTransactionBuilder _builder =
        new(new OptionsWrapper<NodeOptions>(new NodeOptions()), new Mock<IServiceProvider>().Object,
            NullLogger<FundingTransactionBuilder>.Instance);

    private readonly WalletAddressModel _changeAddress =
        new(AddressType.P2Wpkh, 0, true,
            Bolt3AppendixCVectors.NodeAHtlcPubkey.GetAddress(ScriptPubKeyType.Segwit, Network.Main).ToString());

    private static FundingOutputInfo CreateFundingOutputInfo() =>
        new(s_fundingAmount, Bolt3AppendixCVectors.NodeAFundingPubkey.ToBytes(),
            Bolt3AppendixCVectors.NodeBFundingPubkey.ToBytes());

    private static List<UtxoModel> CreateUtxos(long satoshis) =>
    [
        new(new byte[32], 0, LightningMoney.Satoshis(satoshis), 100, 0, false, AddressType.P2Wpkh)
    ];

    [Fact]
    public void Given_Model_When_Building_Then_FundingOutputInfoIsNotMutated()
    {
        // Arrange
        var fundingOutputInfo = CreateFundingOutputInfo();
        var model = new FundingTransactionModel(CreateUtxos(150_000), fundingOutputInfo, s_fee);

        // Act
        _ = _builder.Build(model);

        // Assert
        Assert.Null(fundingOutputInfo.TransactionId);
        Assert.Null(fundingOutputInfo.Index);
    }

    [Fact]
    public void Given_ChangeSmallerThanFunding_When_Building_Then_FundingOutputIndexPointsToFundingOutput()
    {
        // Arrange
        var fundingOutputInfo = CreateFundingOutputInfo();
        var changeAmount = LightningMoney.Satoshis(48_482);
        var model = new FundingTransactionModel(CreateUtxos(150_000), fundingOutputInfo, s_fee)
        {
            ChangeAddress = _changeAddress,
            ChangeAmount = changeAmount
        };
        var expectedFundingScript = new FundingOutput(s_fundingAmount,
                                                              Bolt3AppendixCVectors.NodeAFundingPubkey,
                                                              Bolt3AppendixCVectors.NodeBFundingPubkey)
           .ScriptPubKey;

        // Act
        var result = _builder.Build(model);
        var tx = Transaction.Load(result.Transaction.RawTxBytes, Network.Main);

        // Assert
        Assert.Equal(2, tx.Outputs.Count);
        Assert.Equal(1, result.FundingOutputIndex); // BIP 69: the smaller change output goes first
        Assert.Equal(expectedFundingScript, tx.Outputs[result.FundingOutputIndex].ScriptPubKey);
        Assert.Equal(s_fundingAmount.Satoshi, tx.Outputs[result.FundingOutputIndex].Value.Satoshi);
        Assert.Equal(changeAmount.Satoshi, tx.Outputs[0].Value.Satoshi);
        Assert.Equal(tx.GetHash().ToBytes(), (byte[])result.Transaction.TxId);
    }

    [Fact]
    public void Given_NoChange_When_Building_Then_FundingOutputIsAtIndexZero()
    {
        // Arrange
        var model = new FundingTransactionModel(CreateUtxos(101_518), CreateFundingOutputInfo(), s_fee);

        // Act
        var result = _builder.Build(model);
        var tx = Transaction.Load(result.Transaction.RawTxBytes, Network.Main);

        // Assert
        Assert.Single(tx.Outputs);
        Assert.Equal(0, result.FundingOutputIndex);
    }

    [Fact]
    public void Given_InputsBelowFundingPlusFee_When_Building_Then_ThrowsInsufficientFundsException()
    {
        // Arrange
        var model = new FundingTransactionModel(CreateUtxos(100_500), CreateFundingOutputInfo(), s_fee);

        // Act / Assert
        var exception = Assert.Throws<InsufficientFundsException>(() => _builder.Build(model));
        Assert.Equal(LightningMoney.Satoshis(101_518), exception.Required);
        Assert.Equal(LightningMoney.Satoshis(100_500), exception.Available);
    }
}