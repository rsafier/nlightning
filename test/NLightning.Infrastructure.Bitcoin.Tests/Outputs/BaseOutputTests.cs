using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Outputs;

using Bitcoin.Outputs;
using Domain.Money;

public class BaseOutputTests
{
    private const string TxIdHex = "8984484a580b825b9972d7adb15050b3ab624ccd731946b3eeddb92f4e7ef6be";

    // BaseOutput is exercised through ChangeOutput (P2WPKH), whose public ctors map 1:1 onto the two base ctors.
    // A private subclass would bind to the protected BaseOutput ctors and ScriptType member, which NL-060 changes.
    private readonly Script _redeemScript =
        Script.FromHex("21034F355BDCB7CC0AF728EF3CCEB9615D90684BB5B2CA5F859AB0F0B704075871AAAD51B2");

    private readonly Script _scriptPubKey =
        Script.FromHex("002032E8DA66B7054D40832C6A7A66DF79D8D7BCCCD5FFA53F5DD1772CB9CB9F3283");

    private readonly LightningMoney _amount = LightningMoney.Satoshis(1_000_000);

    [Fact]
    public void Given_ValidParameters_When_ConstructingBaseOutputWithScriptPubKey_Then_PropertiesAreSetCorrectly()
    {
        // Arrange & Act
        var output = new ChangeOutput(_redeemScript, _scriptPubKey, _amount);

        // Assert
        Assert.Equal(_redeemScript, output.RedeemScript);
        Assert.Equal(_scriptPubKey, output.ScriptPubKey);
        Assert.Equal(_amount, output.Amount);
        Assert.Equal(ScriptType.P2WPKH, output.ScriptType);
        Assert.Equal(uint256.Zero, output.TxIdHash);
        Assert.Equal(0U, output.Index);
    }

    [Fact]
    public void Given_ValidParameters_When_ConstructingBaseOutputWithoutScriptPubKey_Then_ScriptPubKeyIsDerived()
    {
        // Arrange & Act
        var output = new ChangeOutput(_redeemScript, _amount);

        // Assert
        Assert.Equal(_redeemScript, output.RedeemScript);
        Assert.Equal(_redeemScript.WitHash.ScriptPubKey, output.ScriptPubKey);
        Assert.Equal(_amount, output.Amount);
    }

    [Fact]
    public void Given_NullScripts_When_ConstructingBaseOutput_Then_ThrowsArgumentNullException()
    {
        // Arrange, Act & Assert
        Assert.Throws<ArgumentNullException>(() => new ChangeOutput(null!, _scriptPubKey, _amount));
        Assert.Throws<ArgumentNullException>(() => new ChangeOutput(_redeemScript, null!, _amount));
        Assert.Throws<ArgumentNullException>(() => new ChangeOutput(null!, _amount));
    }

    [Fact]
    public void Given_BaseOutput_When_ToTxOutCalled_Then_ReturnsCorrectTxOut()
    {
        // Arrange
        var output = new ChangeOutput(_redeemScript, _scriptPubKey, _amount);

        // Act
        var txOut = output.ToTxOut();

        // Assert
        Assert.Equal(_amount, LightningMoney.Satoshis(txOut.Value.Satoshi));
        Assert.Equal(_scriptPubKey, txOut.ScriptPubKey);
    }

    [Fact]
    public void Given_BaseOutputWithValidTxId_When_ToCoinCalled_Then_ReturnsCorrectCoin()
    {
        // Arrange
        var output = new ChangeOutput(_redeemScript, _scriptPubKey, _amount)
        {
            TransactionId = Convert.FromHexString(TxIdHex),
            Index = 1
        };

        // Act
        var coin = output.ToCoin();

        // Assert
        Assert.Equal(output.TxIdHash, coin.Outpoint.Hash);
        Assert.Equal(output.Index, coin.Outpoint.N);
        Assert.Equal(Money.Satoshis(_amount.Satoshi), coin.Amount);
        Assert.Equal(output.ScriptPubKey, coin.ScriptPubKey);
        Assert.Equal(output.RedeemScript, coin.Redeem);
    }

    [Fact]
    public void Given_BaseOutputWithoutTxId_When_ToCoinCalled_Then_ThrowsInvalidOperationException()
    {
        // Arrange
        var output = new ChangeOutput(_redeemScript, _scriptPubKey, _amount);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => output.ToCoin());

        output.TxIdHash = uint256.One;
        Assert.Throws<InvalidOperationException>(() => output.ToCoin());
    }

    [Fact]
    public void Given_BaseOutputWithZeroAmount_When_ToCoinCalled_Then_ThrowsInvalidOperationException()
    {
        // Arrange
        var output = new ChangeOutput(_redeemScript, _scriptPubKey, LightningMoney.Zero)
        {
            TransactionId = Convert.FromHexString(TxIdHex)
        };

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => output.ToCoin());
    }

    [Fact]
    public void Given_TwoOutputs_When_CompareToIsCalled_Then_UsesTransactionOutputComparer()
    {
        // Arrange
        var output1 = new ChangeOutput(_redeemScript, _scriptPubKey, LightningMoney.Satoshis(1_000_000));
        var output2 = new ChangeOutput(_redeemScript, _scriptPubKey, LightningMoney.Satoshis(2_000_000));

        // Act
        var comparison = output1.CompareTo(output2);

        // Assert - BIP 69: lower amount sorts first
        Assert.True(comparison < 0);
        Assert.Equal(1, output1.CompareTo(null));
    }

    [Fact]
    public void Given_BaseOutput_When_AmountIsSet_Then_AmountIsUpdated()
    {
        // Arrange & Act
        var output = new ChangeOutput(_redeemScript, _scriptPubKey, _amount)
        {
            Amount = LightningMoney.Satoshis(42)
        };

        // Assert
        Assert.Equal(LightningMoney.Satoshis(42), output.Amount);
    }
}