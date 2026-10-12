using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Outputs;

using Bitcoin.Outputs;
using Domain.Money;

public class FundingOutputTests
{
    private const string TxIdHex = "8984484a580b825b9972d7adb15050b3ab624ccd731946b3eeddb92f4e7ef6be";

    // BOLT 3 Appendix B local/remote funding pubkeys and the resulting funding witness script
    private readonly PubKey _localPubKey = new("023da092f6980e58d2c037173180e9a465476026ee50f96695963e8efe436f54eb");
    private readonly PubKey _remotePubKey = new("030e9f7b623d2ccc7c9bd44d66d5ce21ce504c0acf6385a132cec6d3c39fa711c1");

    private readonly Script _expectedWitnessScript = Script.FromHex(
        "5221023da092f6980e58d2c037173180e9a465476026ee50f96695963e8efe436f54eb21030e9f7b623d2ccc7c9bd44d66d5ce21ce504c0acf6385a132cec6d3c39fa711c152ae");

    private readonly LightningMoney _amount = LightningMoney.Satoshis(10_000_000);

    [Fact]
    public void Given_ValidParameters_When_ConstructingFundingOutput_Then_PropertiesAreSetCorrectly()
    {
        // Arrange & Act
        var fundingOutput = new FundingOutput(_amount, _localPubKey, _remotePubKey);

        // Assert
        Assert.Equal(_localPubKey, fundingOutput.LocalPubKey);
        Assert.Equal(_remotePubKey, fundingOutput.RemotePubKey);
        Assert.Equal(_amount, fundingOutput.Amount);
        Assert.Equal(ScriptType.P2WSH, fundingOutput.ScriptType);
    }

    [Fact]
    public void Given_Bolt3Keys_When_ConstructingFundingOutput_Then_WitnessScriptMatchesAppendixB()
    {
        // Arrange & Act
        var fundingOutput = new FundingOutput(_amount, _localPubKey, _remotePubKey);

        // Assert
        Assert.Equal(_expectedWitnessScript, fundingOutput.RedeemScript);
        Assert.Equal(_expectedWitnessScript.WitHash.ScriptPubKey, fundingOutput.ScriptPubKey);
        Assert.True(fundingOutput.ScriptPubKey.IsScriptType(ScriptType.P2WSH));
    }

    [Fact]
    public void Given_IdenticalPubKeys_When_ConstructingFundingOutput_Then_ThrowsArgumentException()
    {
        // Arrange, Act & Assert
        var exception =
            Assert.Throws<ArgumentException>(() => new FundingOutput(_amount, _localPubKey, _localPubKey));
        Assert.Contains("Public keys must be different", exception.Message);
    }

    [Fact]
    public void Given_ZeroAmount_When_ConstructingFundingOutput_Then_ThrowsArgumentOutOfRangeException()
    {
        // Arrange, Act & Assert
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
                                                                       new FundingOutput(
                                                                           LightningMoney.Zero, _localPubKey,
                                                                           _remotePubKey));
        Assert.Contains("Funding amount must be greater than zero", exception.Message);
    }

    [Fact]
    public void Given_FundingOutput_When_ToCoinCalled_Then_ReturnsCorrectScriptCoin()
    {
        // Arrange
        var fundingOutput = new FundingOutput(_amount, _localPubKey, _remotePubKey)
        {
            TransactionId = Convert.FromHexString(TxIdHex),
            Index = 1
        };

        // Act
        var coin = fundingOutput.ToCoin();

        // Assert
        Assert.Equal(fundingOutput.TxIdHash, coin.Outpoint.Hash);
        Assert.Equal(fundingOutput.Index, coin.Outpoint.N);
        Assert.Equal(Money.Satoshis(_amount.Satoshi), coin.Amount);
        Assert.Equal(fundingOutput.ScriptPubKey, coin.ScriptPubKey);
        Assert.Equal(fundingOutput.RedeemScript, coin.Redeem);
    }

    [Fact]
    public void Given_TwoFundingOutputs_When_ComparingThem_Then_LowerAmountSortsFirst()
    {
        // Arrange
        var output1 = new FundingOutput(LightningMoney.Satoshis(1_000_000), _localPubKey, _remotePubKey);
        var output2 = new FundingOutput(LightningMoney.Satoshis(2_000_000), _localPubKey, _remotePubKey);

        // Act
        var comparison = output1.CompareTo(output2);

        // Assert
        Assert.True(comparison < 0);
    }

    [Fact]
    public void Given_DifferentPubKeyOrder_When_ConstructingFundingOutputs_Then_CreatesIdenticalScripts()
    {
        // Arrange & Act
        var fundingOutput1 = new FundingOutput(_amount, _localPubKey, _remotePubKey);
        var fundingOutput2 = new FundingOutput(_amount, _remotePubKey, _localPubKey);

        // Assert - BOLT 3: pubkeys are sorted lexicographically in the 2-of-2 script
        Assert.Equal(fundingOutput1.RedeemScript, fundingOutput2.RedeemScript);
        Assert.Equal(fundingOutput1.ScriptPubKey, fundingOutput2.ScriptPubKey);
    }
}