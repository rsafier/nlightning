using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Payments.Routing;

using Application.Payments.Routing;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Models;
using Domain.Money;
using Domain.Node.Options;

/// <summary>
/// ONION M4-T6 route choice: direct peer, or our channel to hint[0].node then the hint hops; per-hop amounts (BOLT 7
/// fee, rounded down) and CLTVs (BOLT 4), final <c>outgoing_cltv_value</c> = height + c + 3.
/// </summary>
public class HintRouteBuilderTests
{
    private const uint Height = 700;
    private const ushort FinalDelta = 40;
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(50_000_123);

    private static readonly CompactPubKey s_us = new TestNodeKeyManager(0x01).NodeId;
    private static readonly CompactPubKey s_bob = new TestNodeKeyManager(0x02).NodeId;
    private static readonly CompactPubKey s_carol = new TestNodeKeyManager(0x03).NodeId;
    private static readonly CompactPubKey s_david = new TestNodeKeyManager(0x04).NodeId;
    private static readonly CompactPubKey s_erin = new TestNodeKeyManager(0x05).NodeId;

    private static readonly ShortChannelId s_scidBc = new(100, 1, 0);
    private static readonly ShortChannelId s_scidCd = new(101, 2, 1);

    private readonly NodeOptions _nodeOptions = new();

    private HintRouteBuilder CreateBuilder() => new(Options.Create(_nodeOptions));

    private static PaymentTarget Target(params IReadOnlyList<RoutingInfo>[] hints) =>
        new(s_david, Enumerable.Repeat((byte)0x11, 32).ToArray(), Enumerable.Repeat((byte)0x22, 32).ToArray(),
            s_amount, FinalDelta, hints);

    private static RoutingInfo BobHint() => new(s_bob, s_scidBc, 1_000, 100, 40);
    private static RoutingInfo CarolHint() => new(s_carol, s_scidCd, 2_000, 500, 40);

    [Fact]
    public void Given_PayeeIsOurPeer_When_Built_Then_SingleFinalHopWithoutFee()
    {
        // Act: a hint exists but the direct channel wins
        var route = CreateBuilder().Build(Target([BobHint(), CarolHint()]), s_amount, Height, s_us,
                                          peer => peer == s_david || peer == s_bob);

        // Assert
        var hop = Assert.Single(route.Hops);
        Assert.Equal(s_david, hop.NodeId);
        Assert.True(hop.IsFinal);
        Assert.Equal(s_amount, hop.AmountToForward);
        Assert.Equal(Height + FinalDelta + HintRouteBuilder.FinalCltvSafetyOffset, hop.OutgoingCltvValue);
        Assert.Equal(s_amount, route.FirstHopAmount);
        Assert.Equal(hop.OutgoingCltvValue, route.FirstHopCltvExpiry);
        Assert.True(route.Fee.IsZero);
        Assert.Equal(s_david, route.FirstHopNodeId);
    }

    [Fact]
    public void Given_AbcdTwoEntryHint_When_Built_Then_AmountsFollowBolt7AndCltvsAddEachDelta()
    {
        // Arrange: roadmap §3 fee_C = 2000 + floor(X*500/1e6), amt_BC = X + fee_C, fee_B = 1000 + floor(amt_BC*100/1e6)
        const ulong x = 50_000_123;
        const ulong feeC = 2_000 + x * 500 / 1_000_000;
        const ulong amountBc = x + feeC;
        const ulong feeB = 1_000 + amountBc * 100 / 1_000_000;
        const uint finalCltv = Height + FinalDelta + 3;

        // Act
        var route = CreateBuilder().Build(Target([BobHint(), CarolHint()]), s_amount, Height, s_us,
                                          peer => peer == s_bob);

        // Assert
        Assert.Equal(3, route.Hops.Count);
        Assert.Equal(new RouteHop(s_bob, LightningMoney.MilliSatoshis(amountBc), finalCltv + 40, s_scidBc),
                     route.Hops[0]);
        Assert.Equal(new RouteHop(s_carol, s_amount, finalCltv, s_scidCd), route.Hops[1]);
        Assert.Equal(new RouteHop(s_david, s_amount, finalCltv, null), route.Hops[2]);
        Assert.Equal(amountBc + feeB, route.FirstHopAmount.MilliSatoshi);
        Assert.Equal(finalCltv + 80, route.FirstHopCltvExpiry);
        Assert.Equal(feeB + feeC, route.Fee.MilliSatoshi);
        Assert.Equal(s_amount, route.Amount);
        Assert.Equal(s_david, route.PayeeNodeId);
    }

    [Fact]
    public void Given_HintStartingAtOurPeer_When_Built_Then_OnlyThatHopChargesAFee()
    {
        // Arrange: ABCD variant (c), Bob pays David with the hint [{Carol, C-D}] over his channel to Carol
        const ulong feeC = 2_000 + 50_000_123UL * 500 / 1_000_000;

        // Act
        var route = CreateBuilder().Build(Target([CarolHint()]), s_amount, Height, s_bob, peer => peer == s_carol);

        // Assert
        Assert.Equal(2, route.Hops.Count);
        Assert.Equal(s_carol, route.FirstHopNodeId);
        Assert.Equal(s_scidCd, route.Hops[0].OutgoingShortChannelId);
        Assert.Equal(feeC, route.Fee.MilliSatoshi);
        Assert.Equal(Height + FinalDelta + 3 + 40, route.FirstHopCltvExpiry);
    }

    [Fact]
    public void Given_HintThatPassesThroughUs_When_Built_Then_RouteStartsAfterOurEntry()
    {
        // Act: Bob is "us", the hint is [{Bob, B-C}, {Carol, C-D}], so Bob's channel B-C leads to Carol
        var route = CreateBuilder().Build(Target([BobHint(), CarolHint()]), s_amount, Height, s_bob,
                                          peer => peer == s_carol);

        // Assert
        Assert.Equal([s_carol, s_david], route.Hops.Select(h => h.NodeId));
        Assert.Equal(s_scidCd, route.Hops[0].OutgoingShortChannelId);
    }

    [Fact]
    public void Given_FirstHintUnreachable_When_Built_Then_NextHintIsUsed()
    {
        // Act
        var route = CreateBuilder().Build(Target([new RoutingInfo(s_erin, s_scidBc, 0, 0, 40)], [CarolHint()]),
                                          s_amount, Height, s_us, peer => peer == s_carol);

        // Assert
        Assert.Equal(s_carol, route.FirstHopNodeId);
    }

    [Fact]
    public void Given_NoUsableChannel_When_Built_Then_InvalidOperationExplainsEveryCandidate()
    {
        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => CreateBuilder().Build(
                                                                     Target([BobHint(), CarolHint()]), s_amount,
                                                                     Height, s_us, _ => false));

        // Assert
        Assert.Contains("direct", exception.Message);
        Assert.Contains("route hint 0", exception.Message);
    }

    [Fact]
    public void Given_PayeeNotAPeerAndNoHints_When_TryBuild_Then_FalseWithReason()
    {
        // Act
        var found = CreateBuilder().TryBuild(Target(), s_amount, Height, s_us, _ => false, out var route,
                                             out var reason);

        // Assert
        Assert.False(found);
        Assert.Null(route);
        Assert.Contains("no usable channel", reason);
    }

    [Fact]
    public void Given_TotalCltvBeyondMaxDistance_When_Built_Then_CandidateIsRejected()
    {
        // Arrange: c + 3 + 40 + 40 = 123 blocks
        _nodeOptions.Routing.MaxCltvExpiryDistance = 122;

        // Act
        var found = CreateBuilder().TryBuild(Target([BobHint(), CarolHint()]), s_amount, Height, s_us,
                                             peer => peer == s_bob, out _, out var reason);

        // Assert
        Assert.False(found);
        Assert.Contains("exceeds 122", reason);
    }

    [Fact]
    public void Given_ZeroAmountOrPayingOurselves_When_Built_Then_ArgumentException()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act / Assert
        Assert.Throws<ArgumentException>(() => builder.Build(Target(), LightningMoney.Zero, Height, s_us, _ => true));
        Assert.Throws<ArgumentException>(() => builder.Build(Target(), s_amount, Height, s_david, _ => true));
    }
}