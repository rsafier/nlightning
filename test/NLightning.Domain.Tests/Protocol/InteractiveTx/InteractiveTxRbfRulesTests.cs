using System.Security.Cryptography;

namespace NLightning.Domain.Tests.Protocol.InteractiveTx;

using Domain.Bitcoin.ValueObjects;
using Domain.Money;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Models;
using static InteractiveTxTestData;

/// <summary>
/// Rule tables for <see cref="InteractiveTxRbfRules"/> (IT1-T4, IT-RBF-01): the <c>tx_init_rbf</c> feerate rule with
/// the additive 25 sat/kw floor of the current BOLT 2 text (lightning/bolts PR #1327) and the double-spend of every
/// previous attempt.
/// </summary>
public class InteractiveTxRbfRulesTests
{
    #region Feerate (BOLT 2 tx_init_rbf)

    [Theory]
    [InlineData(520U, 545U)] // the spec's example: max(520 * 25 / 24, 520 + 25) = max(541, 545) = 545
    [InlineData(253U, 278U)] // max(263, 278): the additive floor wins at low feerates
    [InlineData(600U, 625U)] // max(625, 625): both rules meet
    [InlineData(601U, 626U)] // max(626.04 -> 626, 626)
    [InlineData(1_000U, 1_041U)] // max(1041.67 -> 1041, 1025): the multiplicative rule wins, rounded down
    [InlineData(10_000U, 10_416U)]
    [InlineData(0U, 25U)]
    [InlineData(uint.MaxValue, uint.MaxValue)] // saturates instead of overflowing
    public void Given_PreviousFeerate_When_ComputingTheMinimumNextFeerate_Then_MaxOfTheTwoRules(uint previous,
        uint expected)
    {
        // Act
        var minimum = InteractiveTxRbfRules.GetMinimumNextFeerate(previous);

        // Assert
        Assert.Equal(expected, minimum);
    }

    [Theory]
    [InlineData(545U, 520U, null)]
    [InlineData(544U, 520U, "IT-RBF-01")] // above 25/24 x 520 = 541 but under 520 + 25
    [InlineData(541U, 520U, "IT-RBF-01")] // the old multiplicative-only rule would accept it
    [InlineData(520U, 520U, "IT-RBF-01")]
    [InlineData(278U, 253U, null)]
    [InlineData(277U, 253U, "IT-RBF-01")]
    [InlineData(1_041U, 1_000U, null)]
    [InlineData(1_040U, 1_000U, "IT-RBF-01")] // above 1,000 + 25 but under 25/24 x 1,000
    [InlineData(100_000U, 520U, null)]
    public void Given_ProposedAndPreviousFeerate_When_CheckingTxInitRbf_Then_MatchesTheTable(uint proposed,
        uint previous, string? expected)
    {
        // Arrange
        // (BOLT 2 tx_init_rbf recipient: "MUST respond with tx_abort if: the feerate is not greater than or equal to
        // the maximum of: 25/24 times the feerate of the last successfully constructed transaction, rounded down; 25
        // sat per kw greater than the feerate of the last successfully constructed transaction")

        // Act
        var violation = InteractiveTxRbfRules.CheckFeerate(proposed, previous);

        // Assert
        Assert.Equal(expected, violation?.RequirementId);
    }

    #endregion

    #region Double-spend of previous attempts

    private static InteractiveTxInput In(ulong serialId, InteractiveTxParty party, int seed, uint vout = 0) =>
        new(serialId, party, PrevTxId(PrevTx(seed)), vout, Sequence, LightningMoney.Satoshis(100_000), P2Wpkh,
            PrevTx(seed), false);

    private static InteractiveTxInput SharedIn(InteractiveTxParty party) =>
        new(0, party, FundingTxId, 1, Sequence, LightningMoney.Satoshis(1_000_000), FundingScript, null, true);

    private static ConstructedInteractiveTx Attempt(params InteractiveTxInput[] inputs) =>
        new(new TxId(SHA256.HashData(inputs.SelectMany(i => (byte[])i.PrevTxId).ToArray())), [0x02], 120,
            inputs, [], 1_000, null);

    private static (TxId, uint)[] Outpoints(params int[] seeds) =>
        [.. seeds.Select(s => (PrevTxId(PrevTx(s)), 0U))];

    [Fact]
    public void Given_PartyContributedToEveryAttempt_When_ItReAddsOneInputOfEach_Then_Ok()
    {
        // Arrange
        // (BOLT 2 tx_init_rbf/tx_ack_rbf sender: "If it contributed to previous transactions: MUST ensure that the new
        // transaction double-spends all other attempts, by sending tx_add_input with at least one input from each
        // previous transaction construction attempt.")
        var previous = new[]
        {
            Attempt(In(0, InteractiveTxParty.Remote, 1), In(2, InteractiveTxParty.Remote, 2)),
            Attempt(In(0, InteractiveTxParty.Remote, 3))
        };

        // Act
        var violation = InteractiveTxRbfRules.CheckPartyDoubleSpends(Outpoints(2, 3), InteractiveTxParty.Remote,
                                                                     previous);

        // Assert
        Assert.Null(violation);
    }

    [Fact]
    public void Given_OneInputCoveringTwoAttempts_When_Checking_Then_Ok()
    {
        // Arrange: the same wallet input was in both attempts
        var previous = new[]
        {
            Attempt(In(0, InteractiveTxParty.Remote, 1)),
            Attempt(In(0, InteractiveTxParty.Remote, 1), In(2, InteractiveTxParty.Remote, 2))
        };

        // Act
        var violation = InteractiveTxRbfRules.CheckPartyDoubleSpends(Outpoints(1), InteractiveTxParty.Remote,
                                                                     previous);

        // Assert
        Assert.Null(violation);
    }

    [Fact]
    public void Given_PartyMissesOneAttempt_When_Checking_Then_Violation()
    {
        // Arrange
        var previous = new[]
        {
            Attempt(In(0, InteractiveTxParty.Remote, 1)),
            Attempt(In(0, InteractiveTxParty.Remote, 3))
        };

        // Act
        var violation = InteractiveTxRbfRules.CheckPartyDoubleSpends(Outpoints(1), InteractiveTxParty.Remote,
                                                                     previous);

        // Assert
        Assert.Equal("IT-RBF-01", violation?.RequirementId);
    }

    [Fact]
    public void Given_SameTxIdButAnotherVout_When_Checking_Then_NotADoubleSpend()
    {
        // Arrange: outpoints are compared as (txid, vout)
        var previous = new[] { Attempt(In(0, InteractiveTxParty.Remote, 1)) };
        (TxId, uint)[] other = [(PrevTxId(PrevTx(1)), 1U)];

        // Act
        var violation = InteractiveTxRbfRules.CheckPartyDoubleSpends(other, InteractiveTxParty.Remote, previous);

        // Assert
        Assert.Equal("IT-RBF-01", violation?.RequirementId);
    }

    [Fact]
    public void Given_PartyDidNotContributeBefore_When_ItAddsNewInputs_Then_NoDuty()
    {
        // Arrange: only the other side had inputs in the earlier attempt
        var previous = new[] { Attempt(In(0, InteractiveTxParty.Local, 1)) };

        // Act
        var violation = InteractiveTxRbfRules.CheckPartyDoubleSpends(Outpoints(9), InteractiveTxParty.Remote,
                                                                     previous);

        // Assert
        Assert.Null(violation);
    }

    [Fact]
    public void Given_OnlyASharedInputBefore_When_Checking_Then_ThePartyHadNotContributed()
    {
        // Arrange
        var previous = new[] { Attempt(SharedIn(InteractiveTxParty.Remote)) };

        // Act
        var contributed = InteractiveTxRbfRules.HasContributedInputs(previous[0], InteractiveTxParty.Remote);
        var violation = InteractiveTxRbfRules.CheckPartyDoubleSpends([], InteractiveTxParty.Remote, previous);

        // Assert
        Assert.False(contributed);
        Assert.Null(violation);
    }

    [Theory]
    [InlineData(new[] { 1 }, null)] // our old input re-added: every attempt conflicts
    [InlineData(new[] { 2 }, null)] // the peer's old input
    [InlineData(new[] { 7 }, "IT-RBF-01")] // fresh inputs only: both attempts could confirm
    public void Given_NewInputs_When_CheckingTheWholeTransaction_Then_ItMustConflictWithEveryAttempt(int[] seeds,
        string? expected)
    {
        // Arrange
        var previous = new[]
        {
            Attempt(In(0, InteractiveTxParty.Local, 1), In(1, InteractiveTxParty.Remote, 2))
        };
        var inputs = seeds.Select((s, i) => In((ulong)i * 2, InteractiveTxParty.Local, s)).ToList();

        // Act
        var violation = InteractiveTxRbfRules.CheckDoubleSpendsAllAttempts(inputs, previous);

        // Assert
        Assert.Equal(expected, violation?.RequirementId);
    }

    [Fact]
    public void Given_NoPreviousAttempts_When_CheckingTxComplete_Then_Ok()
    {
        // Act
        var violation = InteractiveTxRbfRules.CheckTxComplete([In(0, InteractiveTxParty.Local, 1)], []);

        // Assert
        Assert.Null(violation);
    }

    [Fact]
    public void Given_PeerContributedBefore_When_OnlyWeDoubleSpend_Then_ThePeersDutyStillFails()
    {
        // Arrange: the whole transaction conflicts through our input, but the peer dropped its earlier input
        var previous = new[]
        {
            Attempt(In(0, InteractiveTxParty.Local, 1), In(1, InteractiveTxParty.Remote, 2))
        };
        var inputs = new List<InteractiveTxInput>
        {
            In(0, InteractiveTxParty.Local, 1),
            In(1, InteractiveTxParty.Remote, 8)
        };

        // Act
        var violation = InteractiveTxRbfRules.CheckTxComplete(inputs, previous);

        // Assert
        Assert.NotNull(violation);
        Assert.Equal("IT-RBF-01", violation.RequirementId);
        Assert.Contains("peer", violation.Reason);
    }

    [Fact]
    public void Given_Splice_When_ThePeerDropsItsWalletInputs_Then_TheSharedInputDoubleSpends()
    {
        // Arrange: splicing rationale, "RBF attempts automatically double-spend each other"
        var previous = new[]
        {
            Attempt(SharedIn(InteractiveTxParty.Local), In(1, InteractiveTxParty.Remote, 2))
        };
        var inputs = new List<InteractiveTxInput> { SharedIn(InteractiveTxParty.Local) };

        // Act
        var violation = InteractiveTxRbfRules.CheckTxComplete(inputs, previous);

        // Assert
        Assert.Null(violation);
    }

    [Fact]
    public void Given_SpliceWithoutTheSharedInputOfAPreviousAttempt_When_Checking_Then_Violation()
    {
        // Arrange: an attempt that spent another funding outpoint is not double-spent by this one
        var previous = new[] { Attempt(In(1, InteractiveTxParty.Remote, 2)) };
        var inputs = new List<InteractiveTxInput> { SharedIn(InteractiveTxParty.Local) };

        // Act
        var violation = InteractiveTxRbfRules.CheckTxComplete(inputs, previous);

        // Assert
        Assert.Equal("IT-RBF-01", violation?.RequirementId);
    }

    [Fact]
    public void Given_RbfAttempt_When_TheFullTxCompleteCheckRuns_Then_ItIncludesTheDoubleSpendRule()
    {
        // Arrange: a peer-only negotiation that pays its fee but spends none of its earlier inputs
        var previous = new[] { Attempt(In(1, InteractiveTxParty.Remote, 2)) };
        var inputs = new List<InteractiveTxInput> { In(1, InteractiveTxParty.Remote, 3) };

        // Act
        var withRbf = InteractiveTxRules.CheckTxComplete(inputs, [], null, 253, false, previous);
        var withoutRbf = InteractiveTxRules.CheckTxComplete(inputs, [], null, 253, false, []);

        // Assert
        Assert.Equal("IT-RBF-01", withRbf?.RequirementId);
        Assert.Null(withoutRbf);
    }

    #endregion
}