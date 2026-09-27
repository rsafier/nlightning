using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Payments.Routing;

using Application.Payments.Routing;
using Application.Payments.Send;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Switch;

/// <summary>
/// ONION M5 sender side: <see cref="BlindedRouteComposer"/> (usability, amounts and expiries of the composed route),
/// <see cref="PaymentRoute"/>'s blinded shape, <see cref="PaymentOnionFactory"/>'s blinded payloads and what
/// <see cref="MissionControl"/> learns from a route that ends in a blinded path. The byte-exact proof against the
/// official vector is <c>Integration.Tests/BOLT4/BlindedPaymentSendVectorTests</c>.
/// </summary>
public class BlindedRouteComposerTests
{
    private static readonly CompactPubKey s_carol = new TestNodeKeyManager(0x03).NodeId;
    private static readonly CompactPubKey s_intro = new TestNodeKeyManager(0x04).NodeId;
    private static readonly CompactPubKey s_blinded1 = new TestNodeKeyManager(0x05).NodeId;
    private static readonly CompactPubKey s_blinded2 = new TestNodeKeyManager(0x06).NodeId;
    private static readonly CompactPubKey s_pathKey = new TestNodeKeyManager(0x07).NodeId;
    private static readonly ShortChannelId s_scid = new(101, 2, 1);
    private static readonly Hash s_hash = new(Enumerable.Repeat((byte)0x11, 32).ToArray());
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(1_000_000);
    private static readonly BlindedPayInfo s_payInfo = new(1_000, 100, 60, 1_000, 5_000_000);

    private static BlindedPaymentPath Path(BlindedPayInfo? payInfo = null) =>
        new(new BlindedPath(s_intro, s_pathKey,
                            [
                                new BlindedPathHop(s_blinded1, new byte[] { 1, 2, 3 }),
                                new BlindedPathHop(s_blinded2, new byte[] { 4, 5 })
                            ]), payInfo ?? s_payInfo);

    /// <summary>Us → Carol → the introduction node, which receives the amount plus the path's fee.</summary>
    private static PaymentRoute ToIntroduction(uint introCltv = 600)
    {
        var introAmount = BlindedRouteComposer.GetIntroductionAmount(s_payInfo, s_amount);
        return new PaymentRoute([
                                    new RouteHop(s_carol, introAmount, introCltv, s_scid),
                                    new RouteHop(s_intro, introAmount, introCltv, null)
                                ], introAmount + LightningMoney.MilliSatoshis(10), introCltv + 40, s_hash,
                                new Secret(new byte[32]));
    }

    [Fact]
    public void Given_PayInfo_When_ComputingTheFee_Then_BasePlusProportionalRoundedDown()
    {
        // Arrange / Act / Assert: 1,000 + 1,000,000 * 100 / 1e6 = 1,100; 1,000 + 9,999 * 100 / 1e6 = 1,000
        Assert.Equal(1_100UL, s_payInfo.ComputeFeeMsat(1_000_000));
        Assert.Equal(1_000UL, s_payInfo.ComputeFeeMsat(9_999));
        Assert.Equal(LightningMoney.MilliSatoshis(1_001_100),
                     BlindedRouteComposer.GetIntroductionAmount(s_payInfo, s_amount));
    }

    [Fact]
    public void Given_RelaysAndAFinalDelta_When_Aggregating_Then_RoundedUpTotalsAndSummedDelta()
    {
        // Arrange: one relay (base 1,000, 100 ppm, delta 40) before our hop, final delta 18
        BlindedPaymentRelay[] relays = [new(40, 100, 1_000)];

        // Act
        var (feeBase, feeProportional, cltvDelta) = BlindedPayInfo.Aggregate(relays, 18);

        // Assert
        Assert.Equal(1_000U, feeBase);
        Assert.Equal(100U, feeProportional);
        Assert.Equal((ushort)58, cltvDelta);
    }

    [Fact]
    public void Given_ARouteToTheIntroductionNode_When_Composing_Then_BlindedHopsReplaceIt()
    {
        // Arrange
        var toIntroduction = ToIntroduction();

        // Act
        var route = BlindedRouteComposer.Compose(toIntroduction, Path(), s_amount, pathIndex: 2);

        // Assert: Carol keeps her scid, the introduction node (real id, path key) and the blinded ids follow
        Assert.Equal(3, route.Hops.Count);
        Assert.Equal(1, route.BlindedStartIndex);
        Assert.Equal(2, route.BlindedPathIndex);
        Assert.Equal(1, route.PublicEdgeCount);
        Assert.Equal(s_scid, route.Hops[0].OutgoingShortChannelId);
        Assert.Equal(s_intro, route.Hops[1].NodeId);
        Assert.Equal(s_pathKey, route.Hops[1].CurrentPathKey);
        Assert.True(route.Hops[1].IsBlindedRelay);
        Assert.Equal(s_blinded2, route.Hops[2].NodeId);
        Assert.True(route.Hops[2].IsFinal);
        Assert.Null(route.Hops[2].CurrentPathKey);
        Assert.Equal(s_amount, route.Amount);
        Assert.Equal(s_amount, route.TotalAmount);
        Assert.Equal(600U - 60, route.Hops[2].OutgoingCltvValue);
        Assert.Equal(toIntroduction.FirstHopAmount, route.FirstHopAmount);
        Assert.Equal(toIntroduction.FirstHopAmount - s_amount, route.Fee);
    }

    [Fact]
    public void Given_AComposedRoute_When_CreatingPayloads_Then_OnlyTheFinalHopHasAmountsAndOnlyTheIntroductionAPathKey()
    {
        // Arrange
        var route = BlindedRouteComposer.Compose(ToIntroduction(), Path(), s_amount,
                                                 LightningMoney.MilliSatoshis(3_000_000));

        // Act
        var carol = PaymentOnionFactory.CreatePayload(route.Hops[0], route);
        var intro = PaymentOnionFactory.CreatePayload(route.Hops[1], route);
        var final = PaymentOnionFactory.CreatePayload(route.Hops[2], route);

        // Assert
        Assert.False(carol.IsBlinded);
        Assert.Equal(s_scid, carol.ShortChannelId);
        Assert.True(intro.IsBlinded);
        Assert.Equal(s_pathKey, intro.CurrentPathKey);
        Assert.Null(intro.AmtToForward);
        Assert.Null(intro.OutgoingCltvValue);
        Assert.Equal(new byte[] { 1, 2, 3 }, intro.EncryptedRecipientData!.Value.ToArray());
        Assert.True(final.IsBlinded);
        Assert.Null(final.CurrentPathKey);
        Assert.Null(final.PaymentData);
        Assert.Equal(s_amount, final.AmtToForward);
        Assert.Equal(LightningMoney.MilliSatoshis(3_000_000), final.TotalAmountMsat);
    }

    [Fact]
    public void Given_ARouteNotEndingAtTheIntroductionNode_When_Composing_Then_Throws()
    {
        // Arrange: the path starts at Carol's peer, the route ends at Carol
        var route = new PaymentRoute([new RouteHop(s_carol, s_amount, 600, null)], s_amount, 600, s_hash,
                                     new Secret(new byte[32]));

        // Act / Assert
        Assert.Throws<ArgumentException>(() => BlindedRouteComposer.Compose(route, Path(), s_amount));
    }

    [Fact]
    public void Given_ARouteWithoutThePathsFee_When_Composing_Then_Throws()
    {
        // Arrange: the introduction node would get only the amount
        var route = new PaymentRoute([new RouteHop(s_intro, s_amount, 600, null)], s_amount, 600, s_hash,
                                     new Secret(new byte[32]));

        // Act / Assert
        Assert.Throws<ArgumentException>(() => BlindedRouteComposer.Compose(route, Path(), s_amount));
    }

    [Fact]
    public void Given_AnExpiryBelowThePathsDelta_When_Composing_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => BlindedRouteComposer.Compose(ToIntroduction(introCltv: 59), Path(),
                                                                            s_amount));
    }

    [Theory]
    [InlineData(2_000_000UL, 5_000_000UL, "minimum")]
    [InlineData(1_000UL, 1_000_000UL, "maximum")]
    public void Given_PathLimits_When_CheckingUsable_Then_TheLimitIsNamed(ulong htlcMinimum, ulong htlcMaximum,
                                                                         string expected)
    {
        // Arrange: 1,000,000 msat + 1,100 fee enters the path
        var tight = Path(s_payInfo with
        {
            HtlcMinimumMsat = htlcMinimum,
            HtlcMaximumMsat = htlcMaximum
        });

        // Act
        var reason = BlindedRouteComposer.CheckUsable(tight, s_amount);

        // Assert
        Assert.NotNull(reason);
        Assert.Contains(expected, reason);
        Assert.Null(BlindedRouteComposer.CheckUsable(Path(), s_amount));
    }

    [Fact]
    public void Given_AnUnknownEvenFeature_When_CheckingUsable_Then_Refused()
    {
        // Arrange: bit 8 (the lowest bit of the first of two bytes) is even and unknown; bit 1 is odd
        var path = Path(s_payInfo with { Features = new byte[] { 0x01, 0x02 } });
        var odd = Path(s_payInfo with { Features = new byte[] { 0x02 } });

        // Act / Assert
        Assert.Contains("bit 8", BlindedRouteComposer.CheckUsable(path, s_amount));
        Assert.Null(BlindedRouteComposer.CheckUsable(odd, s_amount));
    }

    [Fact]
    public void Given_BlindedHopsNotAtTheEnd_When_CreatingARoute_Then_Throws()
    {
        // Arrange: a blinded hop followed by a normal one
        RouteHop[] hops =
        [
            new(s_intro, s_amount, 600, null)
            {
                EncryptedRecipientData = new byte[] { 1 }, CurrentPathKey = s_pathKey, IsBlindedRelay = true
            },
            new(s_carol, s_amount, 600, null)
        ];

        // Act / Assert
        Assert.Throws<ArgumentException>(() => new PaymentRoute(hops, s_amount, 600, s_hash,
                                                                new Secret(new byte[32])));
    }

    [Fact]
    public void Given_AFailureInsideTheBlindedPath_When_Recorded_Then_OnlyTheEdgesBeforeItAreLearnt()
    {
        // Arrange
        var time = new SteppedTimeProvider();
        var missionControl = new MissionControl(Options.Create(new PaymentSendOptions()), time);
        var route = BlindedRouteComposer.Compose(ToIntroduction(), Path(), s_amount);

        // Act: invalid_onion_blinding from the introduction node, then a success
        missionControl.RecordFailure(route, 1, FailureCode.InvalidOnionBlinding);
        missionControl.RecordSuccess(route);

        // Assert: Carol's channel carried the amount; nothing is known about the introduction node's channels
        Assert.True(missionControl.TryGetBounds(s_scid, s_carol, s_intro, out var minMsat, out _));
        Assert.Equal(route.Hops[0].AmountToForward.MilliSatoshi, minMsat);
        Assert.Empty(missionControl.GetSnapshot().PenalizedNodes);
    }
}