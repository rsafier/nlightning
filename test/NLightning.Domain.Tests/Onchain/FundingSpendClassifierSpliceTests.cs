namespace NLightning.Domain.Tests.Onchain;

using Domain.Bitcoin.ValueObjects;
using Domain.Onchain.Classifiers;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Protocol.Models;

/// <summary>
/// Splicing plan §3.6, SP2-C-T1: <see cref="FundingSpendClassifier.ClassifyAny"/> classifies a spend against the
/// funding it spends (current, pending splice, retired), and a splice transaction of the channel is
/// <see cref="FundingSpendKind.Splice"/>, never a close.
/// </summary>
public class FundingSpendClassifierSpliceTests
{
    private static readonly CommitmentNumber s_helper = OnchainTestData.AppendixCCommitmentNumberHelper();
    private static readonly TxId s_oldFundingTxId = OnchainTestData.FundingTxId;
    private static readonly TxId s_spliceTxId = OnchainTestData.TxIdOf(0x77);
    private static readonly TxId s_otherSpliceTxId = OnchainTestData.TxIdOf(0x78);
    private static readonly TxId s_localOnOldTxId = OnchainTestData.TxIdOf(0x10);
    private static readonly TxId s_remoteOnOldTxId = OnchainTestData.TxIdOf(0x20);
    private static readonly TxId s_localOnSpliceTxId = OnchainTestData.TxIdOf(0x11);
    private static readonly TxId s_remoteOnSpliceTxId = OnchainTestData.TxIdOf(0x21);
    private static readonly TxId s_otherTxId = OnchainTestData.TxIdOf(0x50);

    // Commitment numbers: local 7, remote 10 on every funding (SP-I3: one number, one commitment per funding)
    private static FundingSpendContext Current(params TxId[] spliceTxIds) =>
        new(s_oldFundingTxId, 0, s_helper, new CommitmentCandidate(7, s_localOnOldTxId),
            new CommitmentCandidate(10, s_remoteOnOldTxId), SpliceTxIds: spliceTxIds);

    private static FundingSpendContext Pending() =>
        new(s_spliceTxId, 1, s_helper, new CommitmentCandidate(7, s_localOnSpliceTxId),
            new CommitmentCandidate(10, s_remoteOnSpliceTxId));

    private static ChainTx SpendOf(TxId fundingTxId, uint vout, ulong number, TxId txId) =>
        new(txId, 2, s_helper.LockTime(number), [new ChainTxInput(fundingTxId, vout, s_helper.Sequence(number), [])],
            []);

    private static ChainTx SpliceTx(TxId txId) =>
        new(txId, 2, 0,
            [
                new ChainTxInput(s_oldFundingTxId, 0, 0xFFFFFFFD, []),
                new ChainTxInput(s_otherTxId, 3, 0xFFFFFFFD, [])
            ], [new ChainTxOutput(1_000_000, [0x00, 0x20, .. new byte[32]])]);

    [Fact]
    public void Given_APendingSpliceTx_When_ItSpendsTheCurrentFunding_Then_Splice()
    {
        // Arrange
        var spender = SpliceTx(s_spliceTxId);

        // Act
        var match = FundingSpendClassifier.ClassifyAny(spender, [Current(s_spliceTxId), Pending()]);

        // Assert
        Assert.NotNull(match);
        Assert.Equal(s_oldFundingTxId, match.Context.FundingTxId);
        Assert.Equal(FundingSpendKind.Splice, match.Classification.Kind);
        Assert.Null(match.Classification.CommitmentNumber);
        Assert.False(match.Classification.IsCommitment);
    }

    [Fact]
    public void Given_AnUnknownMultiInputSpend_When_ItSpendsTheCurrentFunding_Then_UnknownNotSplice()
    {
        // Arrange: shaped like a splice, but not one of ours (a double spend of the funding output with our signature
        // is impossible, so it is an unknown spend: B5-GEN-06)
        var spender = SpliceTx(s_otherSpliceTxId);

        // Act
        var match = FundingSpendClassifier.ClassifyAny(spender, [Current(s_spliceTxId), Pending()]);

        // Assert
        Assert.NotNull(match);
        Assert.Equal(FundingSpendKind.Unknown, match.Classification.Kind);
    }

    [Fact]
    public void Given_OurCommitmentOnTheCurrentFunding_When_ASpliceIsPending_Then_LocalCommitOfTheCurrentFunding()
    {
        // Arrange
        var spender = SpendOf(s_oldFundingTxId, 0, 7, s_localOnOldTxId);

        // Act
        var match = FundingSpendClassifier.ClassifyAny(spender, [Current(s_spliceTxId), Pending()]);

        // Assert
        Assert.NotNull(match);
        Assert.Equal(s_oldFundingTxId, match.Context.FundingTxId);
        Assert.Equal(FundingSpendKind.LocalCommit, match.Classification.Kind);
        Assert.True(match.Classification.TxIdMatched);
    }

    [Theory]
    [InlineData(7UL, true, FundingSpendKind.LocalCommit)]
    [InlineData(10UL, false, FundingSpendKind.RemoteCommit)]
    public void Given_ACommitmentOnThePendingFunding_When_TheSpliceConfirmed_Then_ClassifiedAgainstThatFunding(
        ulong number, bool ours, FundingSpendKind expected)
    {
        // Arrange
        var spender = SpendOf(s_spliceTxId, 1, number, ours ? s_localOnSpliceTxId : s_remoteOnSpliceTxId);

        // Act
        var match = FundingSpendClassifier.ClassifyAny(spender, [Current(s_spliceTxId), Pending()]);

        // Assert
        Assert.NotNull(match);
        Assert.Equal(s_spliceTxId, match.Context.FundingTxId);
        Assert.Equal(expected, match.Classification.Kind);
        Assert.Equal(number, match.Classification.CommitmentNumber);
        Assert.True(match.Classification.TxIdMatched);
    }

    [Fact]
    public void Given_TheCurrentFundingsCommitmentTxId_When_ItIsOnThePendingFundingOutpoint_Then_NotMatchedByTxId()
    {
        // Arrange: a transaction on the pending funding can't be the current funding's commitment (the txid commits
        // to the outpoint); it is judged by the pending funding's candidates only
        var spender = SpendOf(s_spliceTxId, 1, 10, s_remoteOnOldTxId);

        // Act
        var match = FundingSpendClassifier.ClassifyAny(spender, [Current(s_spliceTxId), Pending()]);

        // Assert
        Assert.NotNull(match);
        Assert.Equal(s_spliceTxId, match.Context.FundingTxId);
        Assert.Equal(FundingSpendKind.RemoteCommit, match.Classification.Kind);
        Assert.False(match.Classification.TxIdMatched);
    }

    [Fact]
    public void Given_ARevokedCommitmentOfARetiredFunding_When_Classifying_Then_RevokedAgainstThatFunding()
    {
        // Arrange: after the lock the old funding is retired (its splice is known); the peer broadcasts commitment 4
        // of the old funding (after a reorg that unspends it), below the current number 10
        var retired = new FundingSpendContext(s_oldFundingTxId, 0, s_helper, null,
                                              new CommitmentCandidate(10, default), SpliceTxIds: [s_spliceTxId]);
        var current = new FundingSpendContext(s_spliceTxId, 1, s_helper, new CommitmentCandidate(7, s_localOnSpliceTxId),
                                              new CommitmentCandidate(10, s_remoteOnSpliceTxId));
        var spender = SpendOf(s_oldFundingTxId, 0, 4, s_otherTxId);

        // Act
        var match = FundingSpendClassifier.ClassifyAny(spender, [current, retired]);

        // Assert
        Assert.NotNull(match);
        Assert.Equal(s_oldFundingTxId, match.Context.FundingTxId);
        Assert.Equal(FundingSpendKind.Revoked, match.Classification.Kind);
        Assert.Equal(4UL, match.Classification.CommitmentNumber);
    }

    [Fact]
    public void Given_TheLockedSplice_When_ItIsSeenAgainOnTheRetiredFunding_Then_Splice()
    {
        // Arrange: a replayed block raises the splice's spend of the old funding again after the lock
        var retired = new FundingSpendContext(s_oldFundingTxId, 0, s_helper, null,
                                              new CommitmentCandidate(10, default), SpliceTxIds: [s_spliceTxId]);
        var current = new FundingSpendContext(s_spliceTxId, 1, s_helper, null, new CommitmentCandidate(10, default));

        // Act
        var match = FundingSpendClassifier.ClassifyAny(SpliceTx(s_spliceTxId), [current, retired]);

        // Assert
        Assert.NotNull(match);
        Assert.Equal(s_oldFundingTxId, match.Context.FundingTxId);
        Assert.Equal(FundingSpendKind.Splice, match.Classification.Kind);
    }

    [Fact]
    public void Given_ATransactionSpendingNoFunding_When_Classifying_Then_Null()
    {
        // Arrange
        var spender = SpendOf(s_otherTxId, 0, 7, s_localOnOldTxId);

        // Act
        var match = FundingSpendClassifier.ClassifyAny(spender, [Current(s_spliceTxId), Pending()]);

        // Assert
        Assert.Null(match);
    }

    [Fact]
    public void Given_NoContexts_When_Classifying_Then_NullNeverThrows()
    {
        // Act
        var match = FundingSpendClassifier.ClassifyAny(new ChainTx(default, 0, 0, [], []), []);

        // Assert
        Assert.Null(match);
    }

    [Fact]
    public void Given_ASingleContext_When_Classifying_Then_SameAsClassify()
    {
        // Arrange
        var context = Current();
        var spender = SpendOf(s_oldFundingTxId, 0, 9, s_otherTxId);

        // Act
        var match = FundingSpendClassifier.ClassifyAny(spender, [context]);

        // Assert
        Assert.NotNull(match);
        Assert.Equal(FundingSpendClassifier.Classify(spender, context), match.Classification);
    }
}