using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Outputs;

using Bitcoin.Outputs;
using Domain.Money;

public class ToRemoteOutputTests
{
    private const string TxIdHex = "8984484a580b825b9972d7adb15050b3ab624ccd731946b3eeddb92f4e7ef6be";

    private readonly PubKey _remotePubKey = new("034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa");
    private readonly LightningMoney _amount = LightningMoney.Satoshis(1_000_000);

    [Fact]
    public void Given_ValidParameters_When_ConstructingToRemoteOutput_Then_PropertiesAreSetCorrectly()
    {
        // Arrange & Act
        var toRemoteOutput = new ToRemoteOutput(_amount, false, _remotePubKey);

        // Assert
        Assert.Equal(_remotePubKey, toRemoteOutput.RemotePubKey);
        Assert.Equal(_amount, toRemoteOutput.Amount);
        Assert.NotNull(toRemoteOutput.RedeemScript);
        Assert.NotNull(toRemoteOutput.ScriptPubKey);
    }

    [Fact]
    public void Given_NoAnchorOutputs_When_ConstructingToRemoteOutput_Then_UsesP2WPKH()
    {
        // Arrange & Act
        var toRemoteOutput = new ToRemoteOutput(_amount, false, _remotePubKey);

        // Assert
        Assert.Equal(ScriptType.P2WPKH, toRemoteOutput.ScriptType);
        Assert.Equal(_remotePubKey.WitHash.ScriptPubKey, toRemoteOutput.RedeemScript);
        Assert.Equal(_remotePubKey.WitHash.ScriptPubKey, toRemoteOutput.ScriptPubKey);
    }

    [Fact]
    public void Given_AnchorOutputs_When_ConstructingToRemoteOutput_Then_UsesBolt3DelayedP2WSH()
    {
        // Arrange - BOLT 3 option_anchors to_remote: <remotepubkey> OP_CHECKSIGVERIFY 1 OP_CHECKSEQUENCEVERIFY
        var expectedScript =
            Script.FromHex("21034F355BDCB7CC0AF728EF3CCEB9615D90684BB5B2CA5F859AB0F0B704075871AAAD51B2");

        // Act
        var toRemoteOutput = new ToRemoteOutput(_amount, true, _remotePubKey);

        // Assert
        Assert.Equal(ScriptType.P2WSH, toRemoteOutput.ScriptType);
        Assert.Equal(expectedScript, toRemoteOutput.RedeemScript);
        Assert.Equal(expectedScript.WitHash.ScriptPubKey, toRemoteOutput.ScriptPubKey);
    }

    [Fact]
    public void Given_ToRemoteOutput_When_ToCoinCalled_Then_ReturnsCorrectScriptCoin()
    {
        // Arrange
        var toRemoteOutput = new ToRemoteOutput(_amount, true, _remotePubKey)
        {
            TransactionId = Convert.FromHexString(TxIdHex),
            Index = 1
        };

        // Act
        var coin = toRemoteOutput.ToCoin();

        // Assert
        Assert.Equal(toRemoteOutput.TxIdHash, coin.Outpoint.Hash);
        Assert.Equal(toRemoteOutput.Index, coin.Outpoint.N);
        Assert.Equal(Money.Satoshis(_amount.Satoshi), coin.Amount);
        Assert.Equal(toRemoteOutput.ScriptPubKey, coin.ScriptPubKey);
        Assert.Equal(toRemoteOutput.RedeemScript, coin.Redeem);
    }

    [Fact]
    public void Given_ZeroAmount_When_ConstructingToRemoteOutput_Then_CreatesZeroValueOutput()
    {
        // Arrange & Act
        var toRemoteOutput = new ToRemoteOutput(LightningMoney.Zero, true, _remotePubKey);

        // Assert
        Assert.Equal(LightningMoney.Zero, toRemoteOutput.Amount);
        Assert.Equal(Money.Zero, toRemoteOutput.ToTxOut().Value);
    }

    [Fact]
    public void Given_TwoToRemoteOutputs_When_ComparingThem_Then_LowerAmountSortsFirst()
    {
        // Arrange
        var output1 = new ToRemoteOutput(LightningMoney.Satoshis(1_000_000), true, _remotePubKey);
        var output2 = new ToRemoteOutput(LightningMoney.Satoshis(2_000_000), true, _remotePubKey);

        // Act
        var comparison = output1.CompareTo(output2);

        // Assert
        Assert.True(comparison < 0);
    }

    [Fact]
    public void Given_DifferentPubKeys_When_ConstructingToRemoteOutputs_Then_GeneratesDifferentScripts()
    {
        // Arrange
        var remotePubKey2 = new PubKey("032c0b7cf95324a07d05398b240174dc0c2be444d96b159aa6c7f7b1e668680991");

        // Act
        var output1 = new ToRemoteOutput(_amount, true, _remotePubKey);
        var output2 = new ToRemoteOutput(_amount, true, remotePubKey2);

        // Assert
        Assert.NotEqual(output1.RedeemScript, output2.RedeemScript);
        Assert.NotEqual(output1.ScriptPubKey, output2.ScriptPubKey);
    }
}