namespace NLightning.Domain.Tests.Protocol.InteractiveTx;

using Domain.Money;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Models;
using static InteractiveTxTestData;

/// <summary>IT-SIG-01: who sends <c>tx_signatures</c> first (IT1-T3).</summary>
public class TxSignaturesOrderTests
{
    [Theory]
    [InlineData(1_000UL, 2_000UL, true, true)]
    [InlineData(2_000UL, 1_000UL, true, false)]
    [InlineData(1_000UL, 2_000UL, false, true)]
    [InlineData(2_000UL, 1_000UL, false, false)]
    [InlineData(1_000UL, 1_000UL, true, true)]
    [InlineData(1_000UL, 1_000UL, false, false)]
    [InlineData(0UL, 0UL, true, true)]
    public void Given_ContributedTotals_When_Ordering_Then_LowerTotalThenLowerNodeIdSendsFirst(ulong local,
        ulong remote, bool localHasLowerNodeId, bool expected)
    {
        // Arrange
        // (BOLT 2: "if it has the lowest total satoshis contributed [...] or both peers have contributed equal
        // amounts but it has the lowest node_id (sorted lexicographically): MUST transmit their tx_signatures first")
        var localId = localHasLowerNodeId ? LowNodeId : HighNodeId;
        var remoteId = localHasLowerNodeId ? HighNodeId : LowNodeId;

        // Act
        var first = TxSignaturesOrder.LocalSendsFirst(local, remote, localId, remoteId);

        // Assert
        Assert.Equal(expected, first);
    }

    [Fact]
    public void Given_NodeIdsDifferingAfterThePrefix_When_Tied_Then_ComparedLexicographically()
    {
        // Arrange: 02ff... sorts below 0300...
        var a = new Domain.Crypto.ValueObjects.CompactPubKey([0x02, .. Enumerable.Repeat((byte)0xFF, 32)]);
        var b = new Domain.Crypto.ValueObjects.CompactPubKey([0x03, .. new byte[32]]);

        // Act
        var aFirst = TxSignaturesOrder.LocalSendsFirst(5, 5, a, b);
        var bFirst = TxSignaturesOrder.LocalSendsFirst(5, 5, b, a);

        // Assert
        Assert.True(aFirst);
        Assert.False(bFirst);
    }

    [Fact]
    public void Given_SameNodeIds_When_Tied_Then_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => TxSignaturesOrder.LocalSendsFirst(1, 1, LowNodeId, LowNodeId));
    }

    [Fact]
    public void Given_SpliceSharedInput_When_Totalling_Then_100PercentCountsForTheInitiator()
    {
        // Arrange
        // (BOLT 2 splicing: "100% of the previous channel capacity is attributed to the initiator when computing who
        // must send tx_signatures first (instead of using each node's previous balance)")
        // We are the non-initiator with a large splice-in (900,000) against a 1,000,000 channel where our balance is
        // 600,000: by balances we would have 1,500,000 against 400,000 and sign second; by the rule the initiator
        // has 1,000,000 and we have 900,000, so we sign first.
        var shared = new InteractiveTxInput(0, InteractiveTxParty.Remote, FundingTxId, 1, Sequence,
                                            LightningMoney.Satoshis(1_000_000), FundingScript, null, true);
        var ours = new InteractiveTxInput(1, InteractiveTxParty.Local, PrevTxId(PrevTx(1)), 0, Sequence,
                                          LightningMoney.Satoshis(900_000), P2Wpkh, PrevTx(1), false);

        // Act
        var (local, remote) = TxSignaturesOrder.ContributedTotals([shared, ours]);
        var first = TxSignaturesOrder.LocalSendsFirst([shared, ours], HighNodeId, LowNodeId);

        // Assert
        Assert.Equal(900_000_000UL, local);
        Assert.Equal(1_000_000_000UL, remote);
        Assert.True(first);
    }

    [Fact]
    public void Given_NoInputsOfOurs_When_Ordering_Then_WeSendFirst()
    {
        // Arrange: the peer added one input, we added nothing (0 < anything)
        var theirs = new InteractiveTxInput(0, InteractiveTxParty.Remote, PrevTxId(PrevTx(2)), 0, Sequence,
                                            LightningMoney.Satoshis(10_000), P2Wpkh, PrevTx(2), false);

        // Act
        var first = TxSignaturesOrder.LocalSendsFirst([theirs], HighNodeId, LowNodeId);

        // Assert
        Assert.True(first);
    }
}