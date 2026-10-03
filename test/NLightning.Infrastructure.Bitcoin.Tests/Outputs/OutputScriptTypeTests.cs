using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Outputs;

using Bitcoin.Outputs;
using Domain.Money;

public class OutputScriptTypeTests
{
    private readonly PubKey _localHtlcPubKey =
        new("030d417a46946384f88d5f3337267c5e579765875dc4daca813e21734b140639e7");

    private readonly PubKey _remoteHtlcPubKey =
        new("0394854aa6eab5b2a8122cc726e9dded053a2184d88256816826d6231c068d4a5b");

    private readonly PubKey _revocationPubKey =
        new("0212a140cd0c6539d07cd08dfe09984dec3251ea808b892efeac3ede9402bf2b19");

    private readonly byte[] _paymentHash = new byte[32];
    private readonly LightningMoney _amount = LightningMoney.Satoshis(1_000);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Given_OfferedHtlcOutput_When_ReadingScriptType_Then_IsP2Wsh(bool hasAnchor)
    {
        // Arrange
        var output = new OfferedHtlcOutput(_amount, 500, hasAnchor, _localHtlcPubKey, _paymentHash,
                                           _remoteHtlcPubKey, _revocationPubKey);

        // Act
        var scriptType = output.ScriptType;

        // Assert
        Assert.Equal(ScriptType.P2WSH, scriptType);
        Assert.True(output.ScriptPubKey.IsScriptType(ScriptType.P2WSH));
        Assert.Equal(output.RedeemScript.WitHash.ScriptPubKey, output.ScriptPubKey);
    }

    [Fact]
    public void Given_ToRemoteOutputWithAnchors_When_Constructed_Then_ScriptTypeAndScriptPubKeyAreP2Wsh()
    {
        // Arrange / Act
        var output = new ToRemoteOutput(_amount, true, _remoteHtlcPubKey);

        // Assert
        Assert.Equal(ScriptType.P2WSH, output.ScriptType);
        Assert.Equal(output.RedeemScript.WitHash.ScriptPubKey, output.ScriptPubKey);
    }

    [Fact]
    public void Given_ToRemoteOutputWithoutAnchors_When_Constructed_Then_ScriptTypeAndScriptPubKeyAreP2Wpkh()
    {
        // Arrange / Act
        var output = new ToRemoteOutput(_amount, false, _remoteHtlcPubKey);

        // Assert
        Assert.Equal(ScriptType.P2WPKH, output.ScriptType);
        Assert.Equal(_remoteHtlcPubKey.WitHash.ScriptPubKey, output.ScriptPubKey);
    }
}