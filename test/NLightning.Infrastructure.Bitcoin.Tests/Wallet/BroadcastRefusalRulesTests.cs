namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Bitcoin.Wallet;
using Domain.Onchain.Enums;

public class BroadcastRefusalRulesTests
{
    [Theory]
    [InlineData("bad-txns-inputs-missingorspent")]
    [InlineData("missing-inputs")]
    [InlineData("Missing inputs")]
    [InlineData("bad-txns-in-belowout, value in (0.001) < value out (0.002)")]
    [InlineData("bad-txns-inputs-duplicate")]
    [InlineData("mandatory-script-verify-flag-failed (Signature must be zero for failed CHECK(MULTI)SIG operation)")]
    [InlineData("non-mandatory-script-verify-flag (Witness program hash mismatch)")]
    [InlineData("mempool-script-verify-flag-failed (Signature must be zero for failed CHECK(MULTI)SIG operation)")]
    public void Given_APermanentRejectReason_When_Classified_Then_ItIsPermanent(string reason)
    {
        // Act / Assert
        Assert.True(BroadcastRefusalRules.IsPermanent(new InvalidOperationException(reason)));
    }

    [Theory]
    [InlineData("min relay fee not met, 100 < 141")]
    [InlineData("mempool min fee not met")]
    [InlineData("insufficient fee, rejecting replacement")]
    [InlineData("non-final")]
    [InlineData("non-BIP68-final")]
    [InlineData("txn-mempool-conflict")]
    [InlineData("too-long-mempool-chain")]
    [InlineData("mempool full")]
    [InlineData("bad-txns-premature-spend-of-coinbase")]
    [InlineData("Connection refused")]
    public void Given_ATemporaryRejectReason_When_Classified_Then_ItIsNotPermanent(string reason)
    {
        // Act / Assert
        Assert.False(BroadcastRefusalRules.IsPermanent(new InvalidOperationException(reason)));
    }

    [Fact]
    public void Given_MissingInputs_When_Classified_Then_ItIsAMissingInputsRefusal()
    {
        // Act / Assert
        Assert.True(BroadcastRefusalRules.IsMissingInputs(
                        new InvalidOperationException("bad-txns-inputs-missingorspent")));
        Assert.False(BroadcastRefusalRules.IsMissingInputs(new InvalidOperationException("bad-txns-in-belowout")));
    }

    [Theory]
    [InlineData("bad-txns-inputs-missingorspent", true)]
    [InlineData("Transaction outputs already in utxo set", true)]
    [InlineData("Transaction already in block chain", true)]
    [InlineData("txn-already-known", true)]
    [InlineData("txn-already-in-mempool", false)]
    [InlineData("min relay fee not met, 100 < 141", false)]
    [InlineData("bad-txns-in-belowout", false)]
    public void Given_ARefusal_When_AskedWhetherTheTransactionMayBeConfirmed_Then_OnlyInChainAndMissingInputsMay(
        string reason, bool mayBeConfirmed)
    {
        // Act / Assert (NL-779)
        Assert.Equal(mayBeConfirmed, BroadcastRefusalRules.MayBeConfirmed(new InvalidOperationException(reason)));
    }

    [Theory]
    [InlineData(BroadcastPurpose.Funding, true)]
    [InlineData(BroadcastPurpose.Unspecified, true)]
    [InlineData(BroadcastPurpose.WalletSend, true)]
    [InlineData(BroadcastPurpose.MutualClose, false)]
    [InlineData(BroadcastPurpose.LocalCommitment, false)]
    [InlineData(BroadcastPurpose.HtlcTransaction, false)]
    [InlineData(BroadcastPurpose.Sweep, false)]
    [InlineData(BroadcastPurpose.HtlcClaim, false)]
    [InlineData(BroadcastPurpose.Penalty, false)]
    [InlineData(BroadcastPurpose.AnchorCpfp, false)]
    [InlineData(BroadcastPurpose.PeerCommitment, false)]
    [InlineData(BroadcastPurpose.Splice, true)] // NL-626: as when splices were saved as Funding
    [InlineData(BroadcastPurpose.AnchorSweep, true)] // NL-611: anyone may take anchors
    public void Given_APurpose_When_AskedIfTheMonitorMayAbandonIt_Then_OnlyWalletOnlySpendsMay(BroadcastPurpose purpose,
        bool expected)
    {
        // Act / Assert
        Assert.Equal(expected, BroadcastRefusalRules.MayAbandon(purpose));
    }

    [Theory]
    [InlineData(BroadcastPurpose.Funding, true)]
    [InlineData(BroadcastPurpose.Unspecified, true)]
    [InlineData(BroadcastPurpose.Splice, true)]
    [InlineData(BroadcastPurpose.WalletSend, false)]
    [InlineData(BroadcastPurpose.LocalCommitment, false)]
    [InlineData(BroadcastPurpose.AnchorCpfp, false)]
    public void Given_APurpose_When_AskedIfItIsAFunding_Then_ASpliceCountsAsOneLikeBeforeItHadItsOwnPurpose(
        BroadcastPurpose purpose, bool expected)
    {
        // Act / Assert (NL-626: the splice purpose changes the label only, not the abandonment and lock rules)
        Assert.Equal(expected, BroadcastRefusalRules.IsFunding(purpose));
    }
}