namespace NLightning.Domain.Tests.Channels.Splicing;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.Splicing.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Node;
using Domain.Protocol.Payloads;

/// <summary>
/// Splicing plan SP1-D-T1: the BOLT 2 "Channel Splicing" rules as tables. Each row names the requirement it proves
/// (splicing plan §1.3/§1.4) and the action BOLT 2 prescribes when it is broken.
/// </summary>
public class SpliceRulesTests
{
    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0x5A, 32).ToArray());
    private static readonly CompactPubKey s_key = new([0x02, .. Enumerable.Repeat((byte)0x11, 32)]);
    private static readonly TxId s_fundingTxId = new(Enumerable.Repeat((byte)0x77, 32).ToArray());
    private static readonly TxId s_otherTxId = new(Enumerable.Repeat((byte)0x78, 32).ToArray());

    /// <summary>Everything allowed for the quiescence initiator: 600k sat ours, 400k sat the peer's.</summary>
    private static readonly SpliceConditions s_initiator = new(true, true, true, true, false, false, false, false,
                                                               600_000_000, 400_000_000);

    /// <summary>The same channel seen by the acceptor (the peer is the quiescence initiator).</summary>
    private static readonly SpliceConditions s_acceptor = s_initiator with { LocalIsQuiescenceInitiator = false };

    #region D14 "splice only when both 35 and 63 are negotiated"

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void Given_NegotiatedFeatures_When_Checked_Then_BothQuiesceAndSpliceAreRequired(bool quiesce, bool splice,
        bool expected)
    {
        // Arrange
        var features = new FeatureSet();
        if (quiesce)
            features.SetFeature(Feature.OptionQuiesce, false);
        if (splice)
            features.SetFeature(Feature.OptionSplice, true);

        // Act
        var negotiated = SpliceRules.IsNegotiated(features);

        // Assert
        Assert.Equal(expected, negotiated);
    }

    #endregion

    #region SP-S-01 / SP-S-02 splice_init sender

    public static TheoryData<string, SpliceConditions, long, string?> SendInitRows => new()
    {
        { "allowed splice-in", s_initiator, 100_000, null },
        { "allowed splice-out of the whole balance", s_initiator, -600_000, null },
        { "allowed zero contribution", s_initiator, 0, null },
        { "not negotiated", s_initiator with { IsNegotiated = false }, 100_000, "D14" },
        { "MUST NOT send splice_init if the channel is not quiescent", s_initiator with { IsQuiescent = false },
          100_000, "SP-S-01" },
        { "MUST NOT send splice_init if it is not the quiescence initiator", s_acceptor, 100_000, "SP-S-01" },
        { "MUST NOT send splice_init before sending and receiving channel_ready",
          s_initiator with { ChannelReadyExchanged = false }, 100_000, "SP-S-01" },
        { "MUST NOT send splice_init while another splice is being negotiated",
          s_initiator with { SpliceNegotiating = true }, 100_000, "SP-S-01" },
        { "MUST NOT send splice_init if another splice has been negotiated but not locked",
          s_initiator with { HasUnlockedSplice = true }, 100_000, "SP-S-01" },
        { "MUST NOT send splice_init if it has previously sent shutdown", s_initiator with { ShutdownSent = true },
          100_000, "SP-S-01" },
        { "a received shutdown alone does not stop our splice_init (the receiver's rule)",
          s_initiator with { ShutdownReceived = true }, 100_000, null },
        { "splice-out: the amount subtracted from its balance, above it", s_initiator, -600_001, "SP-S-02" },
        { "splice-out of long.MinValue", s_initiator, long.MinValue, "SP-S-02" }
    };

    [Theory]
    [MemberData(nameof(SendInitRows))]
    public void Given_SendInitRow_When_Checked_Then_RefusedOnlyWhenTheRuleIsBroken(string row,
        SpliceConditions conditions, long contribution, string? expectedRequirement)
    {
        // Act
        var violation = SpliceRules.CheckSendInit(conditions, contribution);

        // Assert
        Assert.True(expectedRequirement == violation?.RequirementId, $"{row}: {violation?.Reason}");
        if (violation is not null)
            Assert.Equal(SpliceRuleAction.Refuse, violation.Action);
    }

    #endregion

    #region SP-R-01 splice_init receiver

    public static TheoryData<string, SpliceConditions, long, bool, string?, SpliceRuleAction?> ReceiveInitRows => new()
    {
        { "accepted splice-in", s_acceptor, 100_000, true, null, null },
        { "accepted splice-out of the sender's whole balance", s_acceptor, -400_000, true, null, null },
        { "not negotiated (D14)", s_acceptor with { IsNegotiated = false }, 100_000, true, "D14",
          SpliceRuleAction.WarningAndClose },
        { "If the channel is not quiescent: warning and close", s_acceptor with { IsQuiescent = false }, 100_000,
          true, "SP-R-01", SpliceRuleAction.WarningAndClose },
        { "If the sending node is not the quiescence initiator: warning and close", s_initiator, 100_000, true,
          "SP-R-01", SpliceRuleAction.WarningAndClose },
        { "If another splice is already being negotiated: warning and close",
          s_acceptor with { SpliceNegotiating = true }, 100_000, true, "SP-R-01", SpliceRuleAction.WarningAndClose },
        { "If another splice has been negotiated but isn't locked yet: warning and close",
          s_acceptor with { HasUnlockedSplice = true }, 100_000, true, "SP-R-01", SpliceRuleAction.WarningAndClose },
        { "If it has received shutdown: warning and close", s_acceptor with { ShutdownReceived = true }, 100_000,
          true, "SP-R-01", SpliceRuleAction.WarningAndClose },
        { "If the funding_feerate_perkw is unacceptable: tx_abort", s_acceptor, 100_000, false, "SP-R-01",
          SpliceRuleAction.TxAbort },
        { "negative contribution above the sender's balance: warning and close", s_acceptor, -400_001, true,
          "SP-R-01", SpliceRuleAction.WarningAndClose },
        { "the feerate row comes before the balance row", s_acceptor, -400_001, false, "SP-R-01",
          SpliceRuleAction.TxAbort }
    };

    [Theory]
    [MemberData(nameof(ReceiveInitRows))]
    public void Given_ReceiveInitRow_When_Checked_Then_TheSpecActionIsReturned(string row,
        SpliceConditions conditions, long contribution, bool feerateAcceptable, string? expectedRequirement,
        SpliceRuleAction? expectedAction)
    {
        // Arrange
        var payload = new SpliceInitPayload(s_channelId, contribution, 2_500, 800, s_key);

        // Act
        var violation = SpliceRules.CheckReceiveInit(conditions, payload, feerateAcceptable);

        // Assert
        Assert.True(expectedRequirement == violation?.RequirementId, $"{row}: {violation?.Reason}");
        Assert.Equal(expectedAction, violation?.Action);
    }

    #endregion

    #region SP-R-02 splice_ack receiver

    public static TheoryData<string, long, bool, string?> ReceiveAckRows => new()
    {
        { "accepted with contribution 0 (MAY)", 0, true, null },
        { "accepted splice-in by the acceptor", 50_000, true, null },
        { "accepted splice-out of the sender's balance", -400_000, true, null },
        { "Otherwise (it has not sent splice_init): warning and close", 0, false, "SP-R-02" },
        { "negative contribution above the sender's balance: warning and close", -400_001, true, "SP-R-02" }
    };

    [Theory]
    [MemberData(nameof(ReceiveAckRows))]
    public void Given_ReceiveAckRow_When_Checked_Then_WarningAndCloseOnlyWhenBroken(string row, long contribution,
                                                                                   bool initSent,
                                                                                   string? expectedRequirement)
    {
        // Arrange
        var payload = new SpliceAckPayload(s_channelId, contribution, s_key);

        // Act
        var violation = SpliceRules.CheckReceiveAck(s_initiator, payload, initSent);

        // Assert
        Assert.True(expectedRequirement == violation?.RequirementId, $"{row}: {violation?.Reason}");
        if (violation is not null)
            Assert.Equal(SpliceRuleAction.WarningAndClose, violation.Action);
    }

    #endregion

    #region SP-TX-01 / SP-TX-02 the shared input

    public static TheoryData<string, bool, bool, uint, int, string?> SharedInputRows => new()
    {
        { "the initiator adds the current funding output without prevtx", true, true, 0, 0, null },
        { "SP-TX-01: added by the non-initiator", false, true, 0, 0, "SP-TX-01" },
        { "SP-TX-01: MUST NOT include prevtx for that shared input", true, true, 0, 250, "SP-TX-01" },
        { "SP-TX-02: shared_input_txid doesn't match the previous funding txid", true, false, 0, 0, "SP-TX-02" },
        { "SP-TX-02: prevtx_vout doesn't match the previous funding output index", true, true, 1, 0, "SP-TX-02" }
    };

    [Theory]
    [MemberData(nameof(SharedInputRows))]
    public void Given_SharedInputRow_When_Checked_Then_TxAbortOnlyWhenBroken(string row, bool senderIsInitiator,
                                                                            bool sameTxId, uint vout, int prevTxLength,
                                                                            string? expectedRequirement)
    {
        // Act
        var violation = SpliceRules.CheckSharedInputAdd(senderIsInitiator, sameTxId ? s_fundingTxId : s_otherTxId,
                                                        vout, prevTxLength, s_fundingTxId, 0);

        // Assert
        Assert.True(expectedRequirement == violation?.RequirementId, $"{row}: {violation?.Reason}");
        if (violation is not null)
            Assert.Equal(SpliceRuleAction.TxAbort, violation.Action);
    }

    #endregion

    #region SP-TX-03 the new funding output

    public static TheoryData<string, long, long, ulong, string?> FundingOutputRows => new()
    {
        { "splice-in by the initiator", 100_000, 0, 1_100_000, null },
        { "splice-out by the initiator", -250_000, 0, 750_000, null },
        { "both contribute", 100_000, -50_000, 1_050_000, null },
        { "the output ignores the acceptor's contribution", 100_000, -50_000, 1_100_000, "SP-TX-03" },
        { "one satoshi short", 100_000, 0, 1_099_999, "SP-TX-03" },
        { "the contributions leave nothing", -1_000_000, 0, 0, "SP-TX-03" }
    };

    [Theory]
    [MemberData(nameof(FundingOutputRows))]
    public void Given_FundingOutputRow_When_Checked_Then_TheAmountIsCapacityPlusContributions(string row,
        long initContribution, long ackContribution, ulong outputAmount, string? expectedRequirement)
    {
        // Act
        var violation = SpliceRules.CheckFundingOutputAmount(1_000_000, initContribution, ackContribution,
                                                             outputAmount);

        // Assert
        Assert.True(expectedRequirement == violation?.RequirementId, $"{row}: {violation?.Reason}");
        if (violation is not null)
            Assert.Equal(SpliceRuleAction.TxAbort, violation.Action);
    }

    [Fact]
    public void Given_ContributionsThatOverflow_When_NewCapacityComputed_Then_Null()
    {
        // Act / Assert
        Assert.Null(SpliceRules.GetNewCapacitySatoshis(ulong.MaxValue, long.MaxValue, 0));
        Assert.Null(SpliceRules.GetNewCapacitySatoshis(10, -10, 0));
        Assert.Equal(15UL, SpliceRules.GetNewCapacitySatoshis(10, 10, -5));
    }

    #endregion

    #region SP-TX-04 require_confirmed_inputs

    [Theory]
    [InlineData(false, false, null)]
    [InlineData(false, true, null)]
    [InlineData(true, true, null)]
    [InlineData(true, false, "SP-TX-04")]
    public void Given_ConfirmedInputsRequired_When_AnInputIsAdded_Then_OnlyAnUnconfirmedOneIsATxAbort(bool required,
        bool confirmed, string? expectedRequirement)
    {
        // Act
        var violation = SpliceRules.CheckInputConfirmation(required, confirmed);

        // Assert
        Assert.Equal(expectedRequirement, violation?.RequirementId);
        if (violation is not null)
            Assert.Equal(SpliceRuleAction.TxAbort, violation.Action);
    }

    #endregion

    #region SP-TX-05 tx_complete

    /// <summary>A splice-out of 200k sat by us: 1M sat channel, 600k ours, reserves 10k each.</summary>
    private static readonly SpliceTxCompleteFacts s_spliceOut = new(1, 1, 799_000, 1_000_000, -201_000, 0,
                                                                     600_000_000, 400_000_000, 10_000, 10_000, true,
                                                                     false, 1_000);

    public static TheoryData<string, SpliceTxCompleteFacts, string?> TxCompleteRows => new()
    {
        { "a valid splice-out", s_spliceOut, null },
        { "not exactly one input spending the current funding transaction (none)",
          s_spliceOut with { SharedInputCount = 0 }, "SP-TX-05" },
        { "not exactly one input spending the current funding transaction (two)",
          s_spliceOut with { SharedInputCount = 2 }, "SP-TX-05" },
        { "not exactly one channel funding output", s_spliceOut with { FundingOutputCount = 2 }, "SP-TX-05" },
        { "the funding output does not apply the contributions", s_spliceOut with { FundingOutputSatoshis = 800_000 },
          "SP-TX-05" },
        { "an RBF paying less than the previous attempt", s_spliceOut with { PreviousAttemptFeeSatoshis = 1_001 },
          "SP-TX-05" },
        { "an RBF paying as much as the previous attempt", s_spliceOut with { PreviousAttemptFeeSatoshis = 1_000 },
          null },
        { "we take out so much we keep less than the reserve of the new capacity",
          s_spliceOut with
          {
              LocalContributionSatoshis = -596_000, FundingOutputSatoshis = 404_000, LocalReserveSatoshis = 5_000
          }, "SP-TX-05" },
        { "D9: 1 % of the new capacity when the announced reserve is lower",
          s_spliceOut with
          {
              LocalContributionSatoshis = -592_000, FundingOutputSatoshis = 408_000, LocalReserveSatoshis = 0
          }, null },
        { "D9: below 1 % of the new capacity",
          s_spliceOut with
          {
              LocalContributionSatoshis = -596_000, FundingOutputSatoshis = 404_000, LocalReserveSatoshis = 0
          }, "SP-TX-05" },
        { "the peer's splice-out below its reserve",
          s_spliceOut with
          {
              LocalContributionSatoshis = 0, RemoteContributionSatoshis = -395_000, FundingOutputSatoshis = 605_000,
              LocalAddedOtherOutput = false, RemoteAddedOtherOutput = true
          }, "SP-TX-05" },
        { "a side without another output is never held to the reserve (the peer splices in a lot)",
          s_spliceOut with
          {
              LocalContributionSatoshis = 5_000_000, RemoteContributionSatoshis = 0, FundingOutputSatoshis = 6_000_000,
              LocalAddedOtherOutput = true, RemoteAddedOtherOutput = false, RemoteBalanceMsat = 1_000
          }, null }
    };

    [Theory]
    [MemberData(nameof(TxCompleteRows))]
    public void Given_TxCompleteRow_When_Checked_Then_TxAbortOnlyWhenBroken(string row, SpliceTxCompleteFacts facts,
                                                                           string? expectedRequirement)
    {
        // Act
        var violation = SpliceRules.CheckTxComplete(facts);

        // Assert
        Assert.True(expectedRequirement == violation?.RequirementId, $"{row}: {violation?.Reason}");
        if (violation is not null)
            Assert.Equal(SpliceRuleAction.TxAbort, violation.Action);
    }

    [Theory]
    [InlineData(10_000UL, 500_000UL, 10_000UL)]
    [InlineData(1_000UL, 500_000UL, 5_000UL)]
    [InlineData(0UL, 99UL, 0UL)]
    public void Given_AnnouncedReserve_When_ReserveComputed_Then_TheLargerOfItAndOnePercent(ulong announced,
        ulong capacity, ulong expected)
    {
        // Act / Assert
        Assert.Equal(expected, SpliceRules.GetReserveSatoshis(announced, capacity));
    }

    [Fact]
    public void Given_ContributionAboveBalance_When_BalanceAfterComputed_Then_Null()
    {
        // Act / Assert
        Assert.Null(SpliceRules.GetBalanceAfterMsat(1_000, -2));
        Assert.Equal(3_000UL, SpliceRules.GetBalanceAfterMsat(1_000, 2));
    }

    #endregion
}