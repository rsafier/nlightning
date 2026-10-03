namespace NLightning.Application.Tests.Channels.Safety;

using Application.Channels.Safety;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Policies;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;

/// <summary>
/// BOLT2 plan N9-T2 deadline table (B2-CLTV-01/03/04/05/06, B2-FWD-03): every rule at its deadline height N - 1, N and
/// N + G. Lives here (not in Domain.Tests) because the lane owns only the Application tests.
/// </summary>
public class HtlcDeadlinePolicyTests
{
    private const uint Cltv = 1_000;
    private const uint G = HtlcDeadlinePolicy.DefaultGraceBlocks;
    private const uint S = HtlcDeadlinePolicy.DefaultFulfillSafetyBlocks;
    private const uint D = 40;

    private static readonly HtlcDeadlinePolicy s_policy = new(G, S, D);
    private static readonly Hash s_hash = new(new byte[32]);
    private static readonly Secret s_preimage = new(new byte[32]);

    public static TheoryData<uint, HtlcDeadlineAction> OfferedRows => new()
    {
        // Offered deadline = cltv_expiry + G = 1002
        { Cltv + G - 1, HtlcDeadlineAction.None },
        { Cltv + G, HtlcDeadlineAction.FailChannel },
        { Cltv + G + G, HtlcDeadlineAction.FailChannel },
        { Cltv, HtlcDeadlineAction.None }
    };

    [Theory]
    [MemberData(nameof(OfferedRows))]
    public void Given_OfferedHtlcInBothCommitments_When_Evaluated_Then_FailsChannelFromCltvPlusG(
        uint height, HtlcDeadlineAction expected)
    {
        // Arrange
        var htlc = Htlc(HtlcDirection.Outgoing, HtlcState.SentAddAckRevocation);

        // Act
        var decision = s_policy.Evaluate(htlc, height);

        // Assert
        Assert.Equal(expected, decision.Action);
        if (expected == HtlcDeadlineAction.FailChannel)
        {
            Assert.Equal("B2-CLTV-03", decision.RequirementId);
            Assert.Equal(Cltv + G, decision.DeadlineHeight);
        }
    }

    [Theory]
    [InlineData(HtlcState.SentAddCommit, HtlcDeadlineAction.FailChannel)] // only in the peer's commitment
    [InlineData(HtlcState.RcvdRemoveHtlc, HtlcDeadlineAction.FailChannel)] // fulfill/fail received, not committed
    [InlineData(HtlcState.RcvdRemoveCommit, HtlcDeadlineAction.FailChannel)] // still in the peer's commitment
    [InlineData(HtlcState.SentAddHtlc, HtlcDeadlineAction.None)] // in no commitment yet
    [InlineData(HtlcState.SentRemoveAckCommit, HtlcDeadlineAction.None)] // gone from both commitments
    [InlineData(HtlcState.RcvdRemoveAckRevocation, HtlcDeadlineAction.None)] // final
    public void Given_OfferedHtlcPastDeadline_When_InEitherCommitmentOrNot_Then_OnlyCommittedOnesFailChannel(
        HtlcState state, HtlcDeadlineAction expected)
    {
        // Arrange
        var htlc = Htlc(HtlcDirection.Outgoing, state);

        // Act
        var decision = s_policy.Evaluate(htlc, Cltv + G + 10);

        // Assert
        Assert.Equal(expected, decision.Action);
    }

    public static TheoryData<uint, HtlcDeadlineAction> FulfilledRows => new()
    {
        // Fulfillment deadline = cltv_expiry - 18 = 982
        { Cltv - S - 1, HtlcDeadlineAction.None },
        { Cltv - S, HtlcDeadlineAction.FailChannel },
        { Cltv - S + G, HtlcDeadlineAction.FailChannel }
    };

    [Theory]
    [MemberData(nameof(FulfilledRows))]
    public void Given_ReceivedHtlcWeFulfilled_When_Evaluated_Then_FailsChannelFromCltvMinusFulfillDeadline(
        uint height, HtlcDeadlineAction expected)
    {
        // Arrange: our fulfill sent, not yet committed by the peer
        var htlc = Htlc(HtlcDirection.Incoming, HtlcState.SentRemoveHtlc, HtlcRemoval.Fulfill(s_preimage));

        // Act
        var decision = s_policy.Evaluate(htlc, height);

        // Assert
        Assert.Equal(expected, decision.Action);
        if (expected == HtlcDeadlineAction.FailChannel)
        {
            Assert.Equal("B2-CLTV-06", decision.RequirementId);
            Assert.Equal(Cltv - S, decision.DeadlineHeight);
        }
    }

    [Fact]
    public void Given_ReceivedHtlcFulfilledAndGoneFromBothCommitments_When_PastDeadline_Then_Nothing()
    {
        // Arrange
        var htlc = Htlc(HtlcDirection.Incoming, HtlcState.RcvdRemoveAckCommit, HtlcRemoval.Fulfill(s_preimage));

        // Act
        var decision = s_policy.Evaluate(htlc, Cltv);

        // Assert
        Assert.Equal(HtlcDeadlineAction.None, decision.Action);
    }

    public static TheoryData<uint, HtlcDeadlineAction> PreimageKnownRows => new()
    {
        // Known preimage, fulfill not committed: same fulfillment deadline, and never a fail-back
        { Cltv - D, HtlcDeadlineAction.None },
        { Cltv - S - 1, HtlcDeadlineAction.None },
        { Cltv - S, HtlcDeadlineAction.FailChannel },
        { Cltv - S + G, HtlcDeadlineAction.FailChannel }
    };

    [Theory]
    [MemberData(nameof(PreimageKnownRows))]
    public void Given_LockedInHtlcWithKnownPreimage_When_Evaluated_Then_FailsChannelAtFulfillDeadlineNeverFailsBack(
        uint height, HtlcDeadlineAction expected)
    {
        // Arrange
        var htlc = Htlc(HtlcDirection.Incoming, HtlcState.RcvdAddAckRevocation);

        // Act
        var decision = s_policy.Evaluate(htlc, height, IncomingHtlcResolution.PreimageKnown);

        // Assert
        Assert.Equal(expected, decision.Action);
    }

    public static TheoryData<uint, HtlcDeadlineAction> UnresolvedRows => new()
    {
        // Fail-back height = cltv_expiry - delta = 960
        { Cltv - D - 1, HtlcDeadlineAction.None },
        { Cltv - D, HtlcDeadlineAction.FailBackUpstream },
        { Cltv - D + G, HtlcDeadlineAction.FailBackUpstream },
        { Cltv - S, HtlcDeadlineAction.FailBackUpstream }, // B2-CLTV-05: past the fulfillment deadline, fail it
        { Cltv + G, HtlcDeadlineAction.FailBackUpstream }
    };

    [Theory]
    [MemberData(nameof(UnresolvedRows))]
    public void Given_UnresolvedLockedInHtlc_When_Evaluated_Then_FailedBackFromCltvMinusDelta(uint height,
        HtlcDeadlineAction expected)
    {
        // Arrange
        var htlc = Htlc(HtlcDirection.Incoming, HtlcState.RcvdAddAckRevocation);

        // Act
        var decision = s_policy.Evaluate(htlc, height, IncomingHtlcResolution.Unresolved);

        // Assert
        Assert.Equal(expected, decision.Action);
        if (expected == HtlcDeadlineAction.FailBackUpstream)
        {
            Assert.Equal("B2-FWD-03", decision.RequirementId);
            Assert.Equal(Cltv - D, decision.DeadlineHeight);
        }
    }

    public static TheoryData<uint, HtlcDeadlineAction> UnresolvedFinalHopRows => new()
    {
        // A payer's final HTLC: cltv_expiry = height + min_final_cltv_expiry (40) + 3, so it arrives at Cltv - 43.
        // The forwarding distance would fail it back 3 blocks later; the final-hop deadline is cltv_expiry - S = 982
        { Cltv - 40 - 3, HtlcDeadlineAction.None },
        { Cltv - D, HtlcDeadlineAction.None },
        { Cltv - S - 1, HtlcDeadlineAction.None },
        { Cltv - S, HtlcDeadlineAction.FailBackUpstream },
        { Cltv + G, HtlcDeadlineAction.FailBackUpstream }
    };

    [Theory]
    [MemberData(nameof(UnresolvedFinalHopRows))]
    public void Given_UnresolvedFinalHopHtlc_When_Evaluated_Then_FailedBackAtFulfillDeadline(uint height,
        HtlcDeadlineAction expected)
    {
        // Arrange
        var htlc = Htlc(HtlcDirection.Incoming, HtlcState.RcvdAddAckRevocation);

        // Act
        var decision = s_policy.Evaluate(htlc, height, IncomingHtlcResolution.UnresolvedFinalHop);

        // Assert
        Assert.Equal(expected, decision.Action);
        if (expected == HtlcDeadlineAction.FailBackUpstream)
        {
            Assert.Equal("B2-CLTV-05", decision.RequirementId);
            Assert.Equal(Cltv - S, decision.DeadlineHeight);
        }
    }

    [Theory]
    [InlineData(Cltv - D)]
    [InlineData(Cltv - S)]
    [InlineData(Cltv + G + 100)]
    public void Given_HtlcContinuedDownstream_When_AnyHeight_Then_NeverFailedUpstream(uint height)
    {
        // Arrange: failing it upstream while the outgoing HTLC can still be fulfilled would lose the amount
        var htlc = Htlc(HtlcDirection.Incoming, HtlcState.RcvdAddAckRevocation);

        // Act
        var decision = s_policy.Evaluate(htlc, height, IncomingHtlcResolution.AwaitingDownstream);

        // Assert
        Assert.Equal(HtlcDeadlineAction.None, decision.Action);
    }

    [Theory]
    [InlineData(HtlcState.RcvdAddCommit)] // not locked in: cannot be removed yet
    [InlineData(HtlcState.SentAddAckCommit)]
    [InlineData(HtlcState.SentRemoveHtlc)] // we already failed it (removal below)
    public void Given_IncomingHtlcNotLockedInOrFailedByUs_When_PastEveryDeadline_Then_Nothing(HtlcState state)
    {
        // Arrange
        var removal = state == HtlcState.SentRemoveHtlc ? HtlcRemoval.Fail(new byte[292]) : null;
        var htlc = Htlc(HtlcDirection.Incoming, state, removal);

        // Act
        var decision = s_policy.Evaluate(htlc, Cltv + G + 10);

        // Assert
        Assert.Equal(HtlcDeadlineAction.None, decision.Action);
    }

    [Fact]
    public void Given_FailBackBelowFulfillDeadline_When_Created_Then_Throws()
    {
        // Act / Assert (B2-CLTV-05 needs the fail-back before the fulfillment deadline)
        Assert.Throws<ArgumentOutOfRangeException>(() => new HtlcDeadlinePolicy(2, 18, 17));
    }

    [Fact]
    public void Given_SmallCltv_When_ComputingDeadlines_Then_NoUnderflow()
    {
        // Act / Assert
        Assert.Equal(0u, s_policy.FulfillDeadline(5));
        Assert.Equal(0u, s_policy.FailBackHeight(5));
        Assert.Equal(uint.MaxValue, s_policy.OfferedDeadline(uint.MaxValue));
    }

    [Fact]
    public void Given_Options_When_CreatingPolicy_Then_FailBackDefaultsToCltvExpiryDelta()
    {
        // Arrange
        var routing = new RoutingOptions { CltvExpiryDelta = 72 };

        // Act
        var byDefault = new ChannelSafetyOptions().CreatePolicy(routing);
        var explicitValue = new ChannelSafetyOptions { FailBackBlocks = 50 }.CreatePolicy(routing);
        var tooSmall = new ChannelSafetyOptions { FailBackBlocks = 3 }.CreatePolicy(routing);

        // Assert
        Assert.Equal(72u, byDefault.FailBackBlocks);
        Assert.Equal(G, byDefault.GraceBlocks);
        Assert.Equal(S, byDefault.FulfillSafetyBlocks);
        Assert.Equal(50u, explicitValue.FailBackBlocks);
        Assert.Equal(S, tooSmall.FailBackBlocks);
    }

    [Fact]
    public void Given_LockedInIncoming_When_AskingIfResolutionNeeded_Then_OnlyNearItsDeadlines()
    {
        // Arrange
        var htlc = Htlc(HtlcDirection.Incoming, HtlcState.RcvdAddAckRevocation);

        // Act / Assert
        Assert.False(s_policy.NeedsIncomingResolution(htlc, Cltv - D - 1));
        Assert.True(s_policy.NeedsIncomingResolution(htlc, Cltv - D));
        Assert.False(s_policy.NeedsIncomingResolution(Htlc(HtlcDirection.Outgoing, HtlcState.SentAddAckRevocation),
                                                      Cltv));
    }

    private static HtlcRecord Htlc(HtlcDirection direction, HtlcState state, HtlcRemoval? removal = null) =>
        new(direction, 7, 50_000_000, s_hash, Cltv, state, removal);
}