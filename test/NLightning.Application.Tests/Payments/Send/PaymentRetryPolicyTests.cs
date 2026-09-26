using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Payments.Send;

using Application.Payments.Routing;
using Application.Payments.Send;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Commitments;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Interfaces;
using Domain.Models;
using Domain.Money;
using Domain.Protocol.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Domain.Routing.Pathfinding;

/// <summary>
/// NL-270: what one failed part teaches the payment (BOLT 4 "Receiving Failure Codes") and whether it may be retried.
/// The part is us → Carol → David, Carol forwarding over <see cref="s_scidCd"/>.
/// </summary>
public class PaymentRetryPolicyTests
{
    private static readonly CompactPubKey s_carol = new TestNodeKeyManager(0x03).NodeId;
    private static readonly CompactPubKey s_david = new TestNodeKeyManager(0x04).NodeId;
    private static readonly ShortChannelId s_scidCd = new(101, 2, 1);

    private static readonly LocalChannelCandidate s_toCarol =
        new(new ChannelId(Enumerable.Repeat((byte)0xC1, 32).ToArray()), s_carol, new ShortChannelId(300, 1, 0));

    private readonly Mock<ILightningSigner> _signer = new();
    private readonly RouteConstraints _constraints = new();

    public PaymentRetryPolicyTests()
    {
        _signer.Setup(s => s.VerifyNodeMessage(It.IsAny<Hash>(), It.IsAny<CompactSignature>(), s_carol))
               .Returns(true);
    }

    private PaymentRetryPolicy Policy() => new(_signer.Object, ChainConstants.Regtest, 6);

    private static PaymentPart Part()
    {
        var target = new PaymentTarget(s_david, Enumerable.Repeat((byte)0x11, 32).ToArray(),
                                       Enumerable.Repeat((byte)0x22, 32).ToArray(), null, 40,
                                       [[new RoutingInfo(s_carol, s_scidCd, 2_000, 500, 40)]]);
        var route = HintRouteBuilder.BuildAlong([new RoutingInfo(s_carol, s_scidCd, 2_000, 500, 40)], target,
                                                LightningMoney.MilliSatoshis(1_000_000), 800);
        return new PaymentPart(s_toCarol, route, [], "route hint 0");
    }

    /// <summary>Carol's update for Carol → David (Carol's direction), as a failure field payload.</summary>
    private static ReadOnlyMemory<byte> CarolUpdate(uint timestamp = 10, uint feeBase = 3_000,
                                                    ShortChannelId? scid = null, bool disabled = false,
                                                    ChainHash? chain = null)
    {
        var carolIsNode2 = ((ReadOnlySpan<byte>)s_carol).SequenceCompareTo(s_david) > 0;
        var flags = (byte)((carolIsNode2 ? ChannelUpdatePayload.ChannelFlagDirection : 0)
                         | (disabled ? ChannelUpdatePayload.ChannelFlagDisable : 0));
        return new ChannelUpdatePayload(ChannelUpdatePayload.EmptySignature, chain ?? ChainConstants.Regtest,
                                        scid ?? s_scidCd, timestamp, ChannelUpdatePayload.MessageFlagMustBeOne, flags,
                                        80, 1_000, feeBase, 1_000, 5_000_000_000).GetBytes();
    }

    private static FailureInterpretation FromCarol(FailureCode code, ReadOnlyMemory<byte>? update = null,
                                                   bool node = false) =>
        new()
        {
            ErringHopIndex = 0,
            IsFinalNode = false,
            Code = code,
            IsNodeFailure = node,
            IsPermanent = ((ushort)code & (ushort)FailureCodeFlags.Perm) != 0,
            ShouldRetry = true,
            ChannelUpdate = update
        };

    private static FailureInterpretation FromDavid(FailureCode code, bool understood = true) =>
        new()
        {
            ErringHopIndex = 1,
            IsFinalNode = true,
            Code = code,
            IsPermanent = ((ushort)code & (ushort)FailureCodeFlags.Perm) != 0,
            ShouldRetry = ((ushort)code & (ushort)FailureCodeFlags.Perm) == 0 && understood
        };

    [Fact]
    public void Given_APermanentFailureFromThePayee_When_Decided_Then_NotRetried()
    {
        // Act
        var (retry, note) = Policy().Decide(Part(), HtlcRemovalKind.Fail,
                                            FromDavid(FailureCode.IncorrectOrUnknownPaymentDetails), _constraints);

        // Assert
        Assert.False(retry);
        Assert.Contains("permanent", note);
    }

    [Fact]
    public void Given_MppTimeoutFromThePayee_When_Decided_Then_RetriedWithoutAvoidingAnything()
    {
        // Act
        var (retry, _) = Policy().Decide(Part(), HtlcRemovalKind.Fail, FromDavid(FailureCode.MppTimeout),
                                         _constraints);

        // Assert
        Assert.True(retry);
        Assert.Empty(_constraints.ExcludedChannels);
        Assert.Empty(_constraints.ExcludedNodes);
    }

    [Fact]
    public void Given_FeeInsufficientWithAValidUpdate_When_Decided_Then_ThePolicyIsUsedForTheChannel()
    {
        // Act
        var (retry, _) = Policy().Decide(Part(), HtlcRemovalKind.Fail,
                                         FromCarol(FailureCode.FeeInsufficient, CarolUpdate()), _constraints);

        // Assert
        Assert.True(retry);
        var policy = _constraints.PolicyOverrides[s_scidCd];
        Assert.Equal((3_000u, 1_000u, (ushort)80, 10u),
                     (policy.FeeBaseMsat, policy.FeeProportionalMillionths, policy.CltvExpiryDelta, policy.Timestamp));
        Assert.Empty(_constraints.ExcludedChannels);
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("scid")]
    [InlineData("chain")]
    [InlineData("none")]
    public void Given_FeeInsufficientWithAnUnusableUpdate_When_Decided_Then_TheChannelIsAvoided(string defect)
    {
        // Arrange
        ReadOnlyMemory<byte>? update = defect switch
        {
            "scid" => CarolUpdate(scid: new ShortChannelId(9, 9, 9)),
            "chain" => CarolUpdate(chain: ChainConstants.Main),
            "none" => null,
            _ => CarolUpdate()
        };
        if (defect == "signature")
            _signer.Setup(s => s.VerifyNodeMessage(It.IsAny<Hash>(), It.IsAny<CompactSignature>(), s_carol))
                   .Returns(false);

        // Act
        var (retry, _) = Policy().Decide(Part(), HtlcRemovalKind.Fail, FromCarol(FailureCode.FeeInsufficient, update),
                                         _constraints);

        // Assert
        Assert.True(retry);
        Assert.Contains(s_scidCd, _constraints.ExcludedChannels);
        Assert.Empty(_constraints.PolicyOverrides);
    }

    [Fact]
    public void Given_AnUpdateNotNewerThanTheOneUsed_When_Decided_Then_TheChannelIsAvoided()
    {
        // Arrange
        Policy().Decide(Part(), HtlcRemovalKind.Fail, FromCarol(FailureCode.FeeInsufficient, CarolUpdate(10)),
                        _constraints);

        // Act: the same update again (the hop keeps refusing our corrected fee)
        Policy().Decide(Part(), HtlcRemovalKind.Fail, FromCarol(FailureCode.FeeInsufficient, CarolUpdate(10)),
                        _constraints);

        // Assert
        Assert.Contains(s_scidCd, _constraints.ExcludedChannels);
    }

    [Fact]
    public void Given_ADisabledUpdate_When_Decided_Then_TheChannelIsAvoided()
    {
        // Act
        Policy().Decide(Part(), HtlcRemovalKind.Fail,
                        FromCarol(FailureCode.IncorrectCltvExpiry, CarolUpdate(disabled: true)), _constraints);

        // Assert
        Assert.Contains(s_scidCd, _constraints.ExcludedChannels);
    }

    [Fact]
    public void Given_TemporaryChannelFailure_When_Decided_Then_TheChannelIsBoundedBelowTheForwardedAmount()
    {
        // Act
        var (retry, _) = Policy().Decide(Part(), HtlcRemovalKind.Fail,
                                         FromCarol(FailureCode.TemporaryChannelFailure), _constraints);

        // Assert: Carol was asked to forward the 1,000,000 msat David receives
        Assert.True(retry);
        Assert.Equal(1_000_000UL, _constraints.ChannelLiquidityBoundsMsat[s_scidCd]);
        Assert.Empty(_constraints.ExcludedChannels);
    }

    [Fact]
    public void Given_ExpiryTooSoon_When_Decided_Then_MoreCltvIsAdded()
    {
        // Act
        var (retry, _) = Policy().Decide(Part(), HtlcRemovalKind.Fail, FromCarol(FailureCode.ExpiryTooSoon),
                                         _constraints);
        Policy().Decide(Part(), HtlcRemovalKind.Fail, FromCarol(FailureCode.ExpiryTooSoon), _constraints);

        // Assert
        Assert.True(retry);
        Assert.Equal(12u, _constraints.ExtraCltvDelta);
    }

    [Fact]
    public void Given_ANodeFailureOfAnIntermediateHop_When_Decided_Then_TheNodeIsAvoided()
    {
        // Act
        var (retry, _) = Policy().Decide(Part(), HtlcRemovalKind.Fail,
                                         FromCarol(FailureCode.TemporaryNodeFailure, node: true), _constraints);

        // Assert
        Assert.True(retry);
        Assert.Contains(s_carol, _constraints.ExcludedNodes);
    }

    [Fact]
    public void Given_UnknownNextPeer_When_Decided_Then_TheChannelIsAvoided()
    {
        // Act
        var (retry, _) = Policy().Decide(Part(), HtlcRemovalKind.Fail, FromCarol(FailureCode.UnknownNextPeer),
                                         _constraints);

        // Assert
        Assert.True(retry);
        Assert.Contains(s_scidCd, _constraints.ExcludedChannels);
    }

    [Theory]
    [InlineData(HtlcRemovalKind.FailMalformed)]
    [InlineData(HtlcRemovalKind.OnchainTimeout)]
    public void Given_OurPeerRejectedTheOnionOrTheChannelClosed_When_Decided_Then_OurChannelIsAvoided(
        HtlcRemovalKind kind)
    {
        // Act
        var (retry, _) = Policy().Decide(Part(), kind, null, _constraints);

        // Assert
        Assert.True(retry);
        Assert.Contains(s_toCarol.ChannelId, _constraints.ExcludedLocalChannels);
    }

    [Fact]
    public void Given_AnErrorNoHopAuthenticated_When_Decided_Then_OurChannelIsAvoided()
    {
        // Act
        var (retry, _) = Policy().Decide(Part(), HtlcRemovalKind.Fail, new FailureInterpretation { ShouldRetry = true },
                                         _constraints);

        // Assert
        Assert.True(retry);
        Assert.Contains(s_toCarol.ChannelId, _constraints.ExcludedLocalChannels);
    }

    [Fact]
    public void Given_AnUpdateFailure_When_Decided_Then_TheGossipSyncIsAskedForTheChannelAndTheGraphPolicyIsPerPayment()
    {
        // Arrange
        var refresher = new Mock<IGossipScidRefresher>();
        var policy = new PaymentRetryPolicy(_signer.Object, ChainConstants.Regtest, 6, null, refresher.Object);

        // Act
        var (retry, _) = policy.Decide(Part(), HtlcRemovalKind.Fail,
                                       FromCarol(FailureCode.FeeInsufficient, CarolUpdate()), _constraints);

        // Assert: the update is this payment's (both override maps), the graph learns only through gossip (D9)
        Assert.True(retry);
        refresher.Verify(r => r.RequestRefresh(s_scidCd), Times.Once);
        var direction = DirectedChannel.Between(s_scidCd, s_carol, s_david);
        var graphPolicy = _constraints.GraphPolicyOverrides[direction];
        Assert.Equal((3_000u, 1_000u, (ushort)80, 10u, direction.Direction),
                     (graphPolicy.FeeBaseMsat, graphPolicy.FeeProportionalMillionths, graphPolicy.CltvExpiryDelta,
                      graphPolicy.Timestamp, graphPolicy.Direction));
    }

    [Theory]
    [InlineData(FailureCode.UnknownNextPeer)]
    [InlineData(FailureCode.PermanentChannelFailure)]
    [InlineData(FailureCode.TemporaryNodeFailure)]
    public void Given_AFailureWithoutTheUpdateBit_When_Decided_Then_NoGossipRefresh(FailureCode code)
    {
        // Arrange
        var refresher = new Mock<IGossipScidRefresher>();
        var policy = new PaymentRetryPolicy(_signer.Object, ChainConstants.Regtest, 6, null, refresher.Object);

        // Act
        policy.Decide(Part(), HtlcRemovalKind.Fail,
                      FromCarol(code, node: ((ushort)code & (ushort)FailureCodeFlags.Node) != 0), _constraints);

        // Assert
        refresher.Verify(r => r.RequestRefresh(It.IsAny<ShortChannelId>()), Times.Never);
    }

    [Fact]
    public void Given_TemporaryChannelFailure_When_Decided_Then_MissionControlBoundsTheChannelForLaterPayments()
    {
        // Arrange
        var missionControl = new MissionControl(Options.Create(new PaymentSendOptions()), TimeProvider.System);
        var policy = new PaymentRetryPolicy(_signer.Object, ChainConstants.Regtest, 6, missionControl);
        var part = Part();

        // Act
        policy.Decide(part, HtlcRemovalKind.Fail, FromCarol(FailureCode.TemporaryChannelFailure), _constraints);

        // Assert: below what Carol could not forward, for every payment (the payment's own bound is separate)
        Assert.True(missionControl.TryGetBounds(s_scidCd, s_carol, s_david, out _, out var max));
        Assert.Equal(part.Route.Hops[0].AmountToForward.MilliSatoshi, max);
    }

    [Fact]
    public void Given_ANodeFailureFromOurPeer_When_Decided_Then_OnlyThisPaymentAvoidsIt()
    {
        // Arrange
        var missionControl = new MissionControl(Options.Create(new PaymentSendOptions()), TimeProvider.System);
        var policy = new PaymentRetryPolicy(_signer.Object, ChainConstants.Regtest, 6, missionControl);

        // Act: Carol is our peer (hop 0)
        policy.Decide(Part(), HtlcRemovalKind.Fail, FromCarol(FailureCode.TemporaryNodeFailure, node: true),
                      _constraints);

        // Assert: no penalty for later payments (her live channel state decides), but this payment avoids her
        Assert.Empty(missionControl.GetSnapshot().PenalizedNodes);
        Assert.Contains(s_carol, _constraints.ExcludedNodes);
    }
}