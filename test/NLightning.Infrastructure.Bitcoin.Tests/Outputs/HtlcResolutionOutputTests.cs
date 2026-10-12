using NBitcoin;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Infrastructure.Bitcoin.Tests.Outputs;

using Bitcoin.Outputs;
using Domain.Money;

public class HtlcResolutionOutputTests
{
    // BOLT 3 Appendix C: every HTLC-success/HTLC-timeout tx pays to this P2WSH
    // (to_local-style script over local_revocation_pubkey / local_delayedpubkey, to_self_delay 144)
    private static readonly Script s_expectedScriptPubKey =
        Script.FromHex("00204adb4e2f00643db396dd120d4e7dc17625f5f2c11a40d857accc862d6b7dd80e");

    [Fact]
    public void Given_Bolt3AppendixCKeys_When_ConstructingHtlcResolutionOutput_Then_ScriptPubKeyMatchesSpec()
    {
        // Arrange
        var amount = LightningMoney.Satoshis(1_000);

        // Act
        var output = new HtlcResolutionOutput(amount, Bolt3AppendixCVectors.NodeADelayedPubkey,
                                              Bolt3AppendixCVectors.NodeARevocationPubkey,
                                              Bolt3AppendixCVectors.LocalDelay);

        // Assert
        Assert.Equal(s_expectedScriptPubKey, output.ScriptPubKey);
    }

    [Fact]
    public void Given_Keys_When_ConstructingHtlcResolutionOutput_Then_RevocationKeyIsInIfBranch()
    {
        // Arrange
        var amount = LightningMoney.Satoshis(1_000);
        var delayedPubKey = Bolt3AppendixCVectors.NodeADelayedPubkey;
        var revocationPubKey = Bolt3AppendixCVectors.NodeARevocationPubkey;

        // Act
        var output = new HtlcResolutionOutput(amount, delayedPubKey, revocationPubKey, 144);
        var ops = output.RedeemScript.ToOps().ToArray();

        // Assert
        Assert.Equal(OpcodeType.OP_IF, ops[0].Code);
        Assert.Equal(revocationPubKey.ToBytes(), ops[1].PushData);
        Assert.Equal(delayedPubKey.ToBytes(), ops[6].PushData);
        Assert.Equal(delayedPubKey, output.LocalDelayedPubKey);
        Assert.Equal(revocationPubKey, output.RevocationPubKey);
    }
}