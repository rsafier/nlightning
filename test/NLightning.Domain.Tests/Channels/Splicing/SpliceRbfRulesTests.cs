namespace NLightning.Domain.Tests.Channels.Splicing;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.Splicing.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Payloads;

/// <summary>
/// Wave SPR, SPR-T1: the BOLT 2 "Channel Splicing" <c>tx_init_rbf</c>/<c>tx_ack_rbf</c> rules as tables (sender,
/// receiver, <c>tx_ack_rbf</c> receiver, the double-spend duty). Each row names the requirement and the action BOLT 2
/// prescribes when it is broken.
/// </summary>
public class SpliceRbfRulesTests
{
    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0x5A, 32).ToArray());
    private static readonly CompactPubKey s_key = new([0x02, .. Enumerable.Repeat((byte)0x11, 32)]);
    private static readonly TxId s_fundingTxId = new(Enumerable.Repeat((byte)0x77, 32).ToArray());
    private static readonly TxId s_spliceTxId = new(Enumerable.Repeat((byte)0x78, 32).ToArray());

    /// <summary>The quiescence initiator of a channel with a pending splice: 600k sat ours, 400k sat the peer's.</summary>
    private static readonly SpliceConditions s_channel = new(true, true, true, true, false, true, false, false,
                                                             600_000_000, 400_000_000);

    /// <summary>One pending attempt at 1,000 sat/kw; nothing locked; no zeroconf; not recent.</summary>
    private static readonly SpliceRbfConditions s_initiator = new(s_channel, 1, 1_000, false, false, false, false,
                                                                  null, 8);

    /// <summary>The same channel seen by the receiver of the peer's tx_init_rbf (the peer is the quiescence
    /// initiator).</summary>
    private static readonly SpliceRbfConditions s_receiver =
        s_initiator with { Channel = s_channel with { LocalIsQuiescenceInitiator = false } };

    #region tx_init_rbf sender

    public static TheoryData<string, SpliceRbfConditions, uint, long, string?> SendRows => new()
    {
        { "allowed: max(floor(25/24 x 1000), 1000 + 25) = 1041", s_initiator, 1_041, 100_000, null },
        { "allowed: a splice-out RBF of the whole balance", s_initiator, 1_041, -600_000, null },
        { "not negotiated", s_initiator with { Channel = s_channel with { IsNegotiated = false } }, 2_000, 0, "D14" },
        { "MUST NOT send tx_init_rbf if the channel is not quiescent",
          s_initiator with { Channel = s_channel with { IsQuiescent = false } }, 2_000, 0, "SPR-T1" },
        { "MUST NOT send tx_init_rbf if it is not the quiescence initiator", s_receiver, 2_000, 0, "SPR-T1" },
        { "MUST NOT send tx_init_rbf if option_zeroconf has been negotiated",
          s_initiator with { ZeroconfNegotiated = true }, 2_000, 0, "SPR-T1" },
        { "MUST NOT send tx_init_rbf if it has previously sent splice_locked",
          s_initiator with { LocalSentSpliceLocked = true }, 2_000, 0, "SP-LK-04" },
        { "the peer's splice_locked does not stop our RBF", s_initiator with { RemoteSentSpliceLocked = true }, 2_000,
          0, null },
        { "no pending splice to replace",
          s_initiator with { Channel = s_channel with { HasUnlockedSplice = false }, PendingAttemptCount = 0 },
          2_000, 0, "SPR-T1" },
        { "a negotiation in progress", s_initiator with { Channel = s_channel with { SpliceNegotiating = true } },
          2_000, 0, "SPR-T1" },
        { "feerate below the multiplicative floor at 1000 (1040 < 1041)", s_initiator, 1_040, 0, "IT-RBF-01" },
        { "feerate below the additive floor at 100 (124 < 125)", s_initiator with { LastAttemptFeeratePerKw = 100 },
          124, 0, "IT-RBF-01" },
        { "additive floor at 100: 125", s_initiator with { LastAttemptFeeratePerKw = 100, MaxRbfAttempts = 20 }, 125,
          0, null },
        { "multiplicative floor at 2400: 2500", s_initiator with { LastAttemptFeeratePerKw = 2_400 }, 2_500, 0, null },
        { "our own cap Splice:MaxRbfAttempts (8 RBF attempts pending)",
          s_initiator with { PendingAttemptCount = 9 }, 2_000, 0, "SPR-T2" },
        { "below our cap (7 RBF attempts pending)", s_initiator with { PendingAttemptCount = 8 }, 2_000, 0, null },
        { "more than 10 RBF attempts: MUST set a feerate that ensures quick confirmation (unknown estimate)",
          s_initiator with { PendingAttemptCount = 12, MaxRbfAttempts = 20 }, 2_000, 0, "SPR-T1" },
        { "more than 10 RBF attempts: below the quick estimate",
          s_initiator with { PendingAttemptCount = 12, MaxRbfAttempts = 20, QuickConfirmationFeeratePerKw = 5_000 },
          4_999, 0, "SPR-T1" },
        { "more than 10 RBF attempts: at the quick estimate",
          s_initiator with { PendingAttemptCount = 12, MaxRbfAttempts = 20, QuickConfirmationFeeratePerKw = 5_000 },
          5_000, 0, null },
        { "exactly 10 RBF attempts: any valid feerate",
          s_initiator with { PendingAttemptCount = 11, MaxRbfAttempts = 20 }, 2_000, 0, null },
        { "a batch above 20 (19 pending + the current, SP-OP-04)",
          s_initiator with { PendingAttemptCount = 19, MaxRbfAttempts = 30, QuickConfirmationFeeratePerKw = 1 },
          2_000, 0, "SP-OP-04" },
        { "a batch of 20 (18 pending + the current + the new one)",
          s_initiator with { PendingAttemptCount = 18, MaxRbfAttempts = 30, QuickConfirmationFeeratePerKw = 1 },
          2_000, 0, null },
        { "splice-out RBF above our balance (SP-S-02)", s_initiator, 2_000, -600_001, "SP-S-02" }
    };

    [Theory]
    [MemberData(nameof(SendRows))]
    public void Given_TxInitRbfSenderConditions_When_Checked_Then_TheRuleDecides(string row,
        SpliceRbfConditions conditions, uint feerate, long contribution, string? expectedRequirement)
    {
        // Act
        var violation = SpliceRules.CheckSendRbf(conditions, feerate, contribution);

        // Assert
        Assert.True(expectedRequirement == violation?.RequirementId, $"{row}: {violation?.Reason}");
        if (violation is not null)
            Assert.Equal(SpliceRuleAction.Refuse, violation.Action);
    }

    [Fact]
    public void Given_WeAreNotTheSpliceInitiator_When_WeAreTheQuiescenceInitiator_Then_WeMaySendTxInitRbf()
    {
        // Arrange: nothing in the conditions names the splice initiator: BOLT 2 "MAY send tx_init_rbf even if it is
        // not the splice initiator"
        var conditions = s_initiator with { Channel = s_channel with { LocalBalanceMsat = 0 } };

        // Act
        var violation = SpliceRules.CheckSendRbf(conditions, 1_041, 0);

        // Assert
        Assert.Null(violation);
    }

    #endregion

    #region tx_init_rbf receiver

    public static TheoryData<string, SpliceRbfConditions, uint, long?, string?, SpliceRuleAction?> ReceiveRows => new()
    {
        { "allowed", s_receiver, 1_041, null, null, null },
        { "allowed: the peer's splice-out of its whole balance", s_receiver, 1_041, -400_000, null, null },
        { "not negotiated (D14)", s_receiver with { Channel = s_receiver.Channel with { IsNegotiated = false } },
          2_000, null, "D14", SpliceRuleAction.WarningAndClose },
        { "the channel is not quiescent",
          s_receiver with { Channel = s_receiver.Channel with { IsQuiescent = false } }, 2_000, null, "SPR-T1",
          SpliceRuleAction.WarningAndClose },
        { "the sending node is not the quiescence initiator", s_initiator, 2_000, null, "SPR-T1",
          SpliceRuleAction.WarningAndClose },
        { "the sender previously sent splice_locked (SP-LK-04, NL-489)",
          s_receiver with { RemoteSentSpliceLocked = true }, 2_000, null, "SP-LK-04",
          SpliceRuleAction.WarningAndClose },
        { "our splice_locked alone does not stop the peer's RBF", s_receiver with { LocalSentSpliceLocked = true },
          2_000, null, null, null },
        { "option_zeroconf has been negotiated", s_receiver with { ZeroconfNegotiated = true }, 2_000, null,
          "SPR-T1", SpliceRuleAction.WarningAndClose },
        { "negative contribution above the sender's balance", s_receiver, 2_000, -400_001, "SPR-T1",
          SpliceRuleAction.WarningAndClose },
        { "the warning rows come before the feerate", s_receiver with { ZeroconfNegotiated = true }, 1, null,
          "SPR-T1", SpliceRuleAction.WarningAndClose },
        { "feerate below the floor: MUST respond with tx_abort", s_receiver, 1_040, null, "IT-RBF-01",
          SpliceRuleAction.TxAbort },
        { "another RBF attempt was created recently: SHOULD tx_abort", s_receiver with { LastAttemptIsRecent = true },
          2_000, null, "SPR-T1", SpliceRuleAction.TxAbort },
        { "more than 10 RBF attempts, not quick enough: SHOULD tx_abort",
          s_receiver with { PendingAttemptCount = 12, QuickConfirmationFeeratePerKw = 5_000 }, 4_999, null, "SPR-T1",
          SpliceRuleAction.TxAbort },
        { "more than 10 RBF attempts, unknown estimate: tx_abort", s_receiver with { PendingAttemptCount = 12 },
          4_999, null, "SPR-T1", SpliceRuleAction.TxAbort },
        { "more than 10 RBF attempts at the quick estimate",
          s_receiver with { PendingAttemptCount = 12, QuickConfirmationFeeratePerKw = 5_000 }, 5_000, null, null,
          null },
        { "our cap Splice:MaxRbfAttempts does not bind the peer", s_receiver with { PendingAttemptCount = 10 },
          2_000, null, null, null },
        { "no pending splice to replace (MAY tx_abort)",
          s_receiver with
          {
              Channel = s_receiver.Channel with { HasUnlockedSplice = false }, PendingAttemptCount = 0
          }, 2_000, null, "SPR-T1", SpliceRuleAction.TxAbort },
        { "a negotiation in progress (MAY tx_abort)",
          s_receiver with { Channel = s_receiver.Channel with { SpliceNegotiating = true } }, 2_000, null, "SPR-T1",
          SpliceRuleAction.TxAbort },
        { "a batch above 20", s_receiver with { PendingAttemptCount = 19, QuickConfirmationFeeratePerKw = 1 }, 2_000,
          null, "SP-OP-04", SpliceRuleAction.TxAbort }
    };

    [Theory]
    [MemberData(nameof(ReceiveRows))]
    public void Given_TxInitRbfReceiverConditions_When_Checked_Then_TheRuleDecides(string row,
        SpliceRbfConditions conditions, uint feerate, long? contribution, string? expectedRequirement,
        SpliceRuleAction? expectedAction)
    {
        // Arrange
        var payload = new TxInitRbfPayload(s_channelId, feerate, 800_000);

        // Act
        var violation = SpliceRules.CheckReceiveRbf(conditions, payload, contribution);

        // Assert
        Assert.True(expectedRequirement == violation?.RequirementId, $"{row}: {violation?.Reason}");
        Assert.Equal(expectedAction, violation?.Action);
    }

    #endregion

    #region "another RBF attempt has been created recently" (NL-520)

    private static readonly DateTimeOffset s_now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<string, uint?, uint?, TimeSpan?, uint, TimeSpan?, bool> RecencyRows => new()
    {
        // row, created at height, tip, attempt age, MinRbfBlocks, MinRbfInterval, recent
        { "same block: recent", 800_000, 800_000, TimeSpan.FromHours(3), 1, null, true },
        { "one block later: not recent", 800_000, 800_001, TimeSpan.FromSeconds(1), 1, null, false },
        { "two blocks needed, one seen", 800_000, 800_001, TimeSpan.FromHours(1), 2, null, true },
        { "two blocks needed, two seen", 800_000, 800_002, TimeSpan.Zero, 2, null, false },
        { "a reorg below the creation height: still recent", 800_000, 799_999, TimeSpan.FromHours(1), 1, null, true },
        { "MinRbfBlocks 0 turns the rule off", 800_000, 800_000, TimeSpan.Zero, 0, null, false },
        { "unknown creation height: never recent", null, 800_000, TimeSpan.Zero, 1, null, false },
        { "creation height 0 (no chain monitor): never recent", 0u, 800_000, TimeSpan.Zero, 1, null, false },
        { "unknown tip: never recent", 800_000, null, TimeSpan.Zero, 1, null, false },
        { "overflow-safe near the top", uint.MaxValue, uint.MaxValue, TimeSpan.Zero, uint.MaxValue, null, true },
        { "MinRbfInterval replaces the block rule: young", 800_000, 800_005, TimeSpan.FromSeconds(3), 1,
          TimeSpan.FromSeconds(5), true },
        { "MinRbfInterval replaces the block rule: old", 800_000, 800_000, TimeSpan.FromSeconds(6), 1,
          TimeSpan.FromSeconds(5), false },
        { "MinRbfInterval zero turns the rule off", 800_000, 800_000, TimeSpan.Zero, 1, TimeSpan.Zero, false },
        { "MinRbfInterval without a creation time", 800_000, 800_000, null, 1, TimeSpan.FromMinutes(1), false }
    };

    [Theory]
    [MemberData(nameof(RecencyRows))]
    public void Given_ThePreviousAttempt_When_CheckingRecency_Then_TheRuleDecides(string row, uint? createdAtHeight,
        uint? tip, TimeSpan? age, uint minRbfBlocks, TimeSpan? minRbfInterval, bool expected)
    {
        // Arrange
        var createdAt = age is { } a ? s_now - a : (DateTimeOffset?)null;

        // Act
        var recent = SpliceRules.IsLastAttemptRecent(createdAtHeight, tip, createdAt, s_now, minRbfBlocks,
                                                     minRbfInterval);

        // Assert
        Assert.True(expected == recent, row);
    }

    #endregion

    #region tx_ack_rbf receiver

    public static TheoryData<string, long?, bool, string?> AckRows => new()
    {
        { "allowed", null, true, null },
        { "allowed: a splice-out of the whole balance", -400_000, true, null },
        { "tx_ack_rbf without our tx_init_rbf", null, false, "SPR-T1" },
        { "negative contribution above the sender's balance", -400_001, true, "SPR-T1" }
    };

    [Theory]
    [MemberData(nameof(AckRows))]
    public void Given_TxAckRbf_When_Checked_Then_TheRuleDecides(string row, long? contribution, bool rbfSent,
                                                                  string? expectedRequirement)
    {
        // Act
        var violation = SpliceRules.CheckReceiveAckRbf(s_initiator, contribution, rbfSent);

        // Assert
        Assert.True(expectedRequirement == violation?.RequirementId, $"{row}: {violation?.Reason}");
        if (violation is not null)
            Assert.Equal(SpliceRuleAction.WarningAndClose, violation.Action);
    }

    #endregion

    #region Double spend (IT-RBF-01: "Since splice transactions always spend the current channel funding output, the RBF attempts automatically double-spend each other")

    [Fact]
    public void Given_AnAttemptSpendingTheCurrentFundingOutput_When_Checked_Then_ItDoubleSpendsEveryAttempt()
    {
        // Act
        var violation = SpliceRules.CheckRbfDoubleSpends(s_fundingTxId, 1, PendingSet());

        // Assert
        Assert.Null(violation);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 2)]
    public void Given_AnAttemptSpendingAnotherOutput_When_Checked_Then_TxAbort(bool sameTxId, uint vout)
    {
        // Arrange
        var txId = sameTxId ? s_fundingTxId : s_spliceTxId;

        // Act
        var violation = SpliceRules.CheckRbfDoubleSpends(txId, vout, PendingSet());

        // Assert
        Assert.NotNull(violation);
        Assert.Equal("IT-RBF-01", violation.RequirementId);
        Assert.Equal(SpliceRuleAction.TxAbort, violation.Action);
    }

    [Fact]
    public void Given_NothingPending_When_CheckingTheDoubleSpend_Then_TxAbort()
    {
        // Act
        var violation = SpliceRules.CheckRbfDoubleSpends(s_fundingTxId, 1, FundingSet.Single(Current()));

        // Assert
        Assert.Equal(SpliceRuleAction.TxAbort, violation?.Action);
    }

    #endregion

    #region SP-TX-05 "an RBF attempt pays at least the previous attempt's fees"

    [Theory]
    [InlineData(999UL, "SP-TX-05")]
    [InlineData(1_000UL, null)]
    [InlineData(1_500UL, null)]
    public void Given_AnRbfAttempt_When_ItsTotalFeeIsComparedToThePrevious_Then_ALowerOneIsAborted(ulong totalFee,
        string? expectedRequirement)
    {
        // Arrange
        var facts = new SpliceTxCompleteFacts(1, 1, 1_000_000, 1_000_000, 0, 0, 600_000_000, 400_000_000, 10_000,
                                              10_000, false, false, totalFee, 1_000);

        // Act
        var violation = SpliceRules.CheckTxComplete(facts);

        // Assert
        Assert.Equal(expectedRequirement, violation?.RequirementId);
    }

    #endregion

    private static ChannelFunding Current() =>
        new(s_fundingTxId, 1, 1_000_000, s_key, s_key, 0, 0, 0, ChannelFundingKind.Initial,
            ChannelFundingStatus.Current);

    private static FundingSet PendingSet() =>
        FundingSet.Single(Current())
                  .AddPending(new ChannelFunding(s_spliceTxId, 0, 1_100_000, s_key, s_key, 1, 100_000_000, 0,
                                                 ChannelFundingKind.Splice, ChannelFundingStatus.Pending, 1_000,
                                                 800_000));
}