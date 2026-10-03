using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Payments.Trampoline;

using Application.Payments.Routing;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Models;
using Domain.Node.Options;

/// <summary>
/// NL-875 TR4-T2: the planner's absolute final expiry and first-hop cap (a trampoline leg's
/// <c>outgoing_cltv_value</c> and its incoming parts' expiry minus the relay's delta).
/// </summary>
public class TrampolinePlannerTests
{
    private const uint Height = 700;
    private const uint Absolute = 900;

    private static readonly CompactPubKey s_us = new TestNodeKeyManager(0x01).NodeId;
    private static readonly CompactPubKey s_carol = new TestNodeKeyManager(0x03).NodeId;
    private static readonly CompactPubKey s_david = new TestNodeKeyManager(0x04).NodeId;

    private static readonly LocalChannelCandidate s_toDavid =
        new(new ChannelId(Enumerable.Repeat((byte)0xD1, 32).ToArray()), s_david, new ShortChannelId(200, 1, 0));

    private static readonly LocalChannelCandidate s_toCarol =
        new(new ChannelId(Enumerable.Repeat((byte)0xC1, 32).ToArray()), s_carol, new ShortChannelId(300, 1, 0));

    private static PaymentTarget Target(params IReadOnlyList<RoutingInfo>[] hints) =>
        new(s_david, Enumerable.Repeat((byte)0x11, 32).ToArray(), Enumerable.Repeat((byte)0x22, 32).ToArray(), null,
            40, hints);

    private static PaymentPlanRequest Request(PaymentTarget target, uint? absolute, uint? cap,
                                              params LocalChannelCandidate[] channels) =>
        new(target, 100_000, 100_000, 10_000, 1, Height, s_us, channels, (_, _) => 1_000_000, new RouteConstraints(),
            10_000, AbsoluteFinalCltv: absolute, MaxFirstHopCltvExpiry: cap);

    [Fact]
    public void Given_AnAbsoluteFinalExpiry_When_PlannedDirect_Then_TheHtlcCarriesItExactly()
    {
        // Act
        var planned = new PaymentRoutePlanner(Options.Create(new NodeOptions()))
           .TryPlan(Request(Target(), Absolute, Absolute, s_toDavid), out var parts, out var reason);

        // Assert: not height + c + 3, but the leg's expiry, and the cap equal to it is enough for a direct channel
        Assert.True(planned, reason);
        var route = Assert.Single(parts!).Route;
        Assert.Equal(Absolute, route.FirstHopCltvExpiry);
        Assert.Equal(Absolute, route.Hops[^1].OutgoingCltvValue);
    }

    [Theory]
    [InlineData(Absolute + 39, false)]
    [InlineData(Absolute + 40, true)]
    public void Given_AFirstHopCap_When_APathNeedsMore_Then_ItIsSkipped(uint cap, bool expected)
    {
        // Arrange: through Carol, whose hint adds 40 blocks
        var target = Target([new RoutingInfo(s_carol, new ShortChannelId(101, 2, 1), 0, 0, 40)]);

        // Act
        var planned = new PaymentRoutePlanner(Options.Create(new NodeOptions()))
           .TryPlan(Request(target, Absolute, cap, s_toCarol), out var parts, out var reason);

        // Assert
        Assert.Equal(expected, planned);
        if (expected)
            Assert.Equal(Absolute + 40, Assert.Single(parts!).Route.FirstHopCltvExpiry);
        else
            Assert.Contains("total CLTV delta", reason);
    }

    [Fact]
    public void Given_AFinalExpiryAlreadyPast_When_Planned_Then_NoRoute()
    {
        // Act
        var planned = new PaymentRoutePlanner(Options.Create(new NodeOptions()))
           .TryPlan(Request(Target(), Height, null, s_toDavid), out _, out var reason);

        // Assert
        Assert.False(planned);
        Assert.Contains("not above the current height", reason);
    }
}