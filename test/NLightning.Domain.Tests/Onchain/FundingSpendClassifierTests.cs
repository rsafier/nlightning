namespace NLightning.Domain.Tests.Onchain;

using Domain.Bitcoin.ValueObjects;
using Domain.Onchain.Classifiers;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Protocol.Models;

/// <summary>
/// BOLT 5 plan O2-T3: every classification row of <see cref="FundingSpendClassifier"/> (§3.3 cases), malformed
/// spenders included.
/// </summary>
public class FundingSpendClassifierTests
{
    private static readonly CommitmentNumber s_helper = OnchainTestData.AppendixCCommitmentNumberHelper();
    private static readonly TxId s_localTxId = OnchainTestData.TxIdOf(0x10);
    private static readonly TxId s_remoteTxId = OnchainTestData.TxIdOf(0x20);
    private static readonly TxId s_remoteNextTxId = OnchainTestData.TxIdOf(0x30);
    private static readonly TxId s_closingTxId = OnchainTestData.TxIdOf(0x40);
    private static readonly TxId s_otherTxId = OnchainTestData.TxIdOf(0x50);
    private static readonly byte[] s_localShutdown = [0x00, 0x14, .. Enumerable.Repeat((byte)0xAA, 20)];
    private static readonly byte[] s_remoteShutdown = [0x00, 0x14, .. Enumerable.Repeat((byte)0xBB, 20)];

    // Local commitment 7, remote commitment 10 current, 11 signed and unacked
    private static FundingSpendContext Context(bool withNext = true, bool withShutdown = false) =>
        new(OnchainTestData.FundingTxId, 0, s_helper, new CommitmentCandidate(7, s_localTxId),
            new CommitmentCandidate(10, s_remoteTxId), withNext ? new CommitmentCandidate(11, s_remoteNextTxId) : null,
            [s_closingTxId], withShutdown ? s_localShutdown : null, withShutdown ? s_remoteShutdown : null);

    [Fact]
    public void Given_OurLocalCommitmentTxId_When_Classifying_Then_LocalCommit()
    {
        // Arrange
        var spender = OnchainTestData.CommitmentSpend(s_helper, 7, s_localTxId);

        // Act
        var result = FundingSpendClassifier.Classify(spender, Context());

        // Assert
        Assert.Equal(FundingSpendKind.LocalCommit, result.Kind);
        Assert.Equal(7UL, result.CommitmentNumber);
        Assert.True(result.TxIdMatched);
        Assert.True(result.IsCommitment);
    }

    [Fact]
    public void Given_RemoteCurrentTxId_When_Classifying_Then_RemoteCommit()
    {
        // Arrange
        var spender = OnchainTestData.CommitmentSpend(s_helper, 10, s_remoteTxId);

        // Act
        var result = FundingSpendClassifier.Classify(spender, Context());

        // Assert
        Assert.Equal(FundingSpendKind.RemoteCommit, result.Kind);
        Assert.Equal(10UL, result.CommitmentNumber);
        Assert.True(result.TxIdMatched);
    }

    [Fact]
    public void Given_RemoteNextTxId_When_Classifying_Then_RemoteNextCommit()
    {
        // Arrange
        var spender = OnchainTestData.CommitmentSpend(s_helper, 11, s_remoteNextTxId);

        // Act
        var result = FundingSpendClassifier.Classify(spender, Context());

        // Assert
        Assert.Equal(FundingSpendKind.RemoteNextCommit, result.Kind);
        Assert.Equal(11UL, result.CommitmentNumber);
        Assert.True(result.TxIdMatched);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(7UL)] // our local number, but not our txid: a revoked peer commitment with the same number
    [InlineData(9UL)]
    public void Given_OlderRemoteNumberWithUnknownTxId_When_Classifying_Then_Revoked(ulong number)
    {
        // Arrange
        var spender = OnchainTestData.CommitmentSpend(s_helper, number, s_otherTxId);

        // Act
        var result = FundingSpendClassifier.Classify(spender, Context());

        // Assert
        Assert.Equal(FundingSpendKind.Revoked, result.Kind);
        Assert.Equal(number, result.CommitmentNumber);
        Assert.False(result.TxIdMatched);
    }

    [Theory]
    [InlineData(12UL, true)]
    [InlineData(11UL, false)] // no unacked commitment: 11 is newer than anything we signed
    [InlineData(CommitmentNumber.MaxValue, true)]
    public void Given_NewerNumber_When_Classifying_Then_FutureRemote(ulong number, bool withNext)
    {
        // Arrange
        var spender = OnchainTestData.CommitmentSpend(s_helper, number, s_otherTxId);

        // Act
        var result = FundingSpendClassifier.Classify(spender, Context(withNext));

        // Assert
        Assert.Equal(FundingSpendKind.FutureRemote, result.Kind);
        Assert.Equal(number, result.CommitmentNumber);
    }

    [Fact]
    public void Given_RemoteCurrentNumberWithOtherTxId_When_Classifying_Then_RemoteCommitUnmatched()
    {
        // Arrange: our rebuild differs from what the peer holds (risk §8.1): outputs must be mapped by script
        var spender = OnchainTestData.CommitmentSpend(s_helper, 10, s_otherTxId);

        // Act
        var result = FundingSpendClassifier.Classify(spender, Context());

        // Assert
        Assert.Equal(FundingSpendKind.RemoteCommit, result.Kind);
        Assert.Equal(10UL, result.CommitmentNumber);
        Assert.False(result.TxIdMatched);
    }

    [Fact]
    public void Given_RemoteNextNumberWithOtherTxId_When_Classifying_Then_RemoteNextUnmatched()
    {
        // Arrange
        var spender = OnchainTestData.CommitmentSpend(s_helper, 11, s_otherTxId);

        // Act
        var result = FundingSpendClassifier.Classify(spender, Context());

        // Assert
        Assert.Equal(FundingSpendKind.RemoteNextCommit, result.Kind);
        Assert.False(result.TxIdMatched);
    }

    [Fact]
    public void Given_KnownClosingTxId_When_Classifying_Then_Mutual()
    {
        // Arrange
        var spender = OnchainTestData.ClosingSpend(s_closingTxId);

        // Act
        var result = FundingSpendClassifier.Classify(spender, Context());

        // Assert
        Assert.Equal(FundingSpendKind.Mutual, result.Kind);
        Assert.Null(result.CommitmentNumber);
        Assert.True(result.TxIdMatched);
        Assert.False(result.IsCommitment);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Given_UnknownClosingPayingAShutdownScript_When_Classifying_Then_Mutual(bool ours)
    {
        // Arrange: a closing fee we signed but did not record (e.g. accepted in the last round before a crash)
        var spender = OnchainTestData.ClosingSpend(s_otherTxId,
                                                   new ChainTxOutput(1000, ours ? s_localShutdown : s_remoteShutdown));

        // Act
        var result = FundingSpendClassifier.Classify(spender, Context(withShutdown: true));

        // Assert
        Assert.Equal(FundingSpendKind.Mutual, result.Kind);
        Assert.False(result.TxIdMatched);
    }

    [Fact]
    public void Given_NonCommitmentSpendWithoutShutdownScript_When_Classifying_Then_Unknown()
    {
        // Arrange: B5-GEN-06, e.g. a leaked funding key
        var spender = OnchainTestData.ClosingSpend(s_otherTxId, new ChainTxOutput(1000, [0x00, 0x14, .. new byte[20]]));

        // Act
        var result = FundingSpendClassifier.Classify(spender, Context(withShutdown: true));

        // Assert
        Assert.Equal(FundingSpendKind.Unknown, result.Kind);
        Assert.Null(result.CommitmentNumber);
    }

    [Theory]
    [InlineData(0x2052193eU, 0x002bb038U)] // bad sequence prefix
    [InlineData(0x0052193eU, 0x802bb038U)] // bad locktime prefix
    [InlineData(0x2052193eU, 0xFFFFFFFFU)]
    public void Given_MalformedCommitmentFields_When_Classifying_Then_UnknownNeverThrows(uint lockTime,
                                                                                           uint sequence)
    {
        // Arrange
        var spender = new ChainTx(s_otherTxId, 2, lockTime,
                                  [new ChainTxInput(OnchainTestData.FundingTxId, 0, sequence, [])], []);

        // Act
        var result = FundingSpendClassifier.Classify(spender, Context());

        // Assert
        Assert.Equal(FundingSpendKind.Unknown, result.Kind);
    }

    [Fact]
    public void Given_TwoInputs_When_Classifying_Then_Unknown()
    {
        // Arrange: a commitment has exactly one input
        var spender = new ChainTx(s_otherTxId, 2, s_helper.LockTime(9),
                                  [
                                      new ChainTxInput(OnchainTestData.FundingTxId, 0, s_helper.Sequence(9), []),
                                      new ChainTxInput(s_otherTxId, 1, 0xFFFFFFFF, [])
                                  ], []);

        // Act
        var result = FundingSpendClassifier.Classify(spender, Context());

        // Assert
        Assert.Equal(FundingSpendKind.Unknown, result.Kind);
    }

    [Fact]
    public void Given_TxNotSpendingFunding_When_Classifying_Then_NotFundingSpend()
    {
        // Arrange: same txid, other vout
        var spender = new ChainTx(s_localTxId, 2, s_helper.LockTime(7),
                                  [new ChainTxInput(OnchainTestData.FundingTxId, 1, s_helper.Sequence(7), [])], []);

        // Act
        var result = FundingSpendClassifier.Classify(spender, Context());

        // Assert
        Assert.Equal(FundingSpendKind.NotFundingSpend, result.Kind);
    }

    [Fact]
    public void Given_EmptyAndDefaultFields_When_Classifying_Then_NeverThrows()
    {
        // Arrange: no inputs, no outputs, default txid
        var spender = new ChainTx(default, 0, 0, [], []);

        // Act
        var result = FundingSpendClassifier.Classify(spender, Context(withShutdown: true));

        // Assert
        Assert.Equal(FundingSpendKind.NotFundingSpend, result.Kind);
    }

    [Fact]
    public void Given_NoRemoteCommitYet_When_ClassifyingNumber0_Then_FutureRemote()
    {
        // Arrange: nothing to compare with (before the first exchange), the number is not ours
        var context = new FundingSpendContext(OnchainTestData.FundingTxId, 0, s_helper, null, null);
        var spender = OnchainTestData.CommitmentSpend(s_helper, 0, s_otherTxId);

        // Act
        var result = FundingSpendClassifier.Classify(spender, context);

        // Assert
        Assert.Equal(FundingSpendKind.FutureRemote, result.Kind);
    }

    [Fact]
    public void Given_AppendixCCommitmentFields_When_Classifying_Then_Number42Revoked()
    {
        // Arrange: Appendix C obscured number 0x2bb038521914 ^ 42 against a peer now at 43
        var context = new FundingSpendContext(OnchainTestData.FundingTxId, 0, s_helper, null,
                                              new CommitmentCandidate(43, s_remoteTxId));
        var spender = new ChainTx(s_otherTxId, 2, OnchainTestData.AppendixCLockTime,
                                  [
                                      new ChainTxInput(OnchainTestData.FundingTxId, 0,
                                                       OnchainTestData.AppendixCSequence, [])
                                  ], []);

        // Act
        var result = FundingSpendClassifier.Classify(spender, context);

        // Assert
        Assert.Equal(FundingSpendKind.Revoked, result.Kind);
        Assert.Equal(42UL, result.CommitmentNumber);
    }
}