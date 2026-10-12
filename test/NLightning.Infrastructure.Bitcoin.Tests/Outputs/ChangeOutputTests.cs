using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Outputs;

using Bitcoin.Outputs;
using Domain.Money;

public class ChangeOutputTests
{
    private const string TxIdHex = "8984484a580b825b9972d7adb15050b3ab624ccd731946b3eeddb92f4e7ef6be";

    private readonly Script _redeemScript =
        Script.FromHex("21034F355BDCB7CC0AF728EF3CCEB9615D90684BB5B2CA5F859AB0F0B704075871AAAD51B2");

    private readonly Script _scriptPubKey =
        Script.FromHex("002032E8DA66B7054D40832C6A7A66DF79D8D7BCCCD5FFA53F5DD1772CB9CB9F3283");

    private readonly LightningMoney _amount = LightningMoney.Satoshis(1_000_000);

    [Fact]
    public void Given_ScriptPubKeyAndAmount_When_ConstructingChangeOutput_Then_PropertiesAreSetCorrectly()
    {
        // Arrange & Act
        var changeOutput = new ChangeOutput(_scriptPubKey, _amount);

        // Assert
        Assert.Equal(_scriptPubKey, changeOutput.ScriptPubKey);
        Assert.Equal(_scriptPubKey, new Script(changeOutput.RedeemBitcoinScript));
        Assert.Equal(_amount, changeOutput.Amount);
        Assert.Equal(ScriptType.P2WPKH, changeOutput.ScriptType);
    }

    [Fact]
    public void Given_RedeemScriptScriptPubKeyAndAmount_When_ConstructingChangeOutput_Then_PropertiesAreSetCorrectly()
    {
        // Arrange & Act
        var changeOutput = new ChangeOutput(_redeemScript, _scriptPubKey, _amount);

        // Assert
        Assert.Equal(_redeemScript, new Script(changeOutput.RedeemBitcoinScript));
        Assert.Equal(_scriptPubKey, changeOutput.ScriptPubKey);
        Assert.Equal(_amount, changeOutput.Amount);
        Assert.Equal(ScriptType.P2WPKH, changeOutput.ScriptType);
    }

    [Fact]
    public void Given_ScriptPubKeyAndNullAmount_When_ConstructingChangeOutput_Then_AmountIsZero()
    {
        // Arrange
        LightningMoney? amount = null;

        // Act
        var changeOutput = new ChangeOutput(_scriptPubKey, amount);

        // Assert
        Assert.Equal(_scriptPubKey, changeOutput.ScriptPubKey);
        Assert.Equal(LightningMoney.Zero, changeOutput.Amount);
        Assert.Equal(ScriptType.P2WPKH, changeOutput.ScriptType);
    }

    [Fact]
    public void Given_RedeemScriptScriptPubKeyAndNullAmount_When_ConstructingChangeOutput_Then_AmountIsZero()
    {
        // Arrange
        LightningMoney? amount = null;

        // Act
        var changeOutput = new ChangeOutput(_redeemScript, _scriptPubKey, amount);

        // Assert
        Assert.Equal(_redeemScript, new Script(changeOutput.RedeemBitcoinScript));
        Assert.Equal(_scriptPubKey, changeOutput.ScriptPubKey);
        Assert.Equal(LightningMoney.Zero, changeOutput.Amount);
    }

    [Fact]
    public void Given_ChangeOutputWithZeroAmount_When_ToCoinCalled_Then_ThrowsInvalidOperationException()
    {
        // Arrange
        var changeOutput = new ChangeOutput(_scriptPubKey)
        {
            TransactionId = Convert.FromHexString(TxIdHex),
            Index = 1
        };

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => changeOutput.ToCoin());
    }

    [Fact]
    public void Given_ChangeOutputWithValidAmount_When_ToCoinCalled_Then_ReturnsCorrectCoin()
    {
        // Arrange - a non-witness-program script is wrapped as P2WSH
        var changeOutput = new ChangeOutput(_redeemScript, _amount)
        {
            TransactionId = Convert.FromHexString(TxIdHex),
            Index = 1
        };

        // Act
        var coin = changeOutput.ToCoin();

        // Assert
        Assert.Equal(_redeemScript.WitHash.ScriptPubKey, changeOutput.ScriptPubKey);
        Assert.Equal(changeOutput.TxIdHash, coin.Outpoint.Hash);
        Assert.Equal(changeOutput.Index, coin.Outpoint.N);
        Assert.Equal(changeOutput.Amount, LightningMoney.Satoshis(coin.Amount.Satoshi));
        Assert.Equal(changeOutput.ScriptPubKey, coin.ScriptPubKey);
    }

    [Fact]
    public void Given_TwoChangeOutputs_When_ComparingThem_Then_LowerAmountSortsFirst()
    {
        // Arrange
        var output1 = new ChangeOutput(_scriptPubKey, LightningMoney.Satoshis(1_000_000));
        var output2 = new ChangeOutput(_scriptPubKey, LightningMoney.Satoshis(2_000_000));

        // Act
        var comparison = output1.CompareTo(output2);

        // Assert
        Assert.True(comparison < 0);
    }
}