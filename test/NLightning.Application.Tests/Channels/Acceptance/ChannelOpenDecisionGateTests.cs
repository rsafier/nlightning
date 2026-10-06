using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Channels.Acceptance;

using Application.Channels.Acceptance;
using Domain.Channels.Acceptance;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.Constants;

/// <summary>NL-1180: the chained gate of external open decisions (LND's <c>ChainedAcceptor</c>).</summary>
public class ChannelOpenDecisionGateTests
{
    internal static readonly ChannelOpenRequest Request =
        new(new CompactPubKey(Convert.FromHexString("02" + new string('a', 64))), ChainConstants.Regtest,
            new ChannelId(new byte[32]), LightningMoney.Satoshis(1_000_000), LightningMoney.Zero,
            LightningMoney.Satoshis(354), LightningMoney.Satoshis(500_000), LightningMoney.Satoshis(10_000),
            LightningMoney.MilliSatoshis(1), 253, 144, 30, 0, null, false);

    private readonly ChannelOpenDecisionGate _gate = new(NullLogger<ChannelOpenDecisionGate>.Instance);

    [Fact]
    public async Task Given_NoDecider_When_Deciding_Then_TheOpenIsAccepted()
    {
        // Act
        var decision = await _gate.DecideAsync(Request, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(_gate.HasDeciders);
        Assert.Same(ChannelOpenDecision.Accepted, decision);
    }

    [Fact]
    public async Task Given_ARejectingDecider_When_Deciding_Then_TheRejectionWinsAndLaterDecidersAreNotAsked()
    {
        // Arrange
        var later = new FixedDecider(ChannelOpenDecision.Accepted);
        using var first = _gate.Register(new FixedDecider(ChannelOpenDecision.Rejected("not today")));
        using var second = _gate.Register(later);

        // Act
        var decision = await _gate.DecideAsync(Request, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(decision.Accept);
        Assert.Equal("not today", decision.Error);
        Assert.Equal(0, later.Calls);
    }

    [Fact]
    public async Task Given_TwoAcceptingDecidersThatDisagree_When_Deciding_Then_TheOpenIsRejected()
    {
        // Arrange
        using var first = _gate.Register(new FixedDecider(new ChannelOpenDecision { Accept = true, ToSelfDelay = 100 }));
        using var second = _gate.Register(new FixedDecider(new ChannelOpenDecision { Accept = true, ToSelfDelay = 200 }));

        // Act
        var decision = await _gate.DecideAsync(Request, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(decision.Accept);
        Assert.Equal(ChannelOpenDecision.GenericRejection, decision.Error);
    }

    [Fact]
    public async Task Given_TwoAcceptingDecidersThatAgree_When_Deciding_Then_TheirValuesAreMerged()
    {
        // Arrange
        using var first = _gate.Register(new FixedDecider(new ChannelOpenDecision { Accept = true, ToSelfDelay = 100 }));
        using var second = _gate.Register(new FixedDecider(new ChannelOpenDecision { Accept = true, MinimumDepth = 2 }));

        // Act
        var decision = await _gate.DecideAsync(Request, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(decision.Accept);
        Assert.Equal((ushort)100, decision.ToSelfDelay);
        Assert.Equal(2U, decision.MinimumDepth);
    }

    [Fact]
    public async Task Given_AThrowingDecider_When_Deciding_Then_TheOpenIsRejected()
    {
        // Arrange
        using var registration = _gate.Register(new ThrowingDecider());

        // Act
        var decision = await _gate.DecideAsync(Request, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(decision.Accept);
    }

    [Fact]
    public async Task Given_ADisposedRegistration_When_Deciding_Then_TheDeciderIsGone()
    {
        // Arrange
        var registration = _gate.Register(new FixedDecider(ChannelOpenDecision.Rejected()));
        registration.Dispose();

        // Act
        var decision = await _gate.DecideAsync(Request, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(_gate.HasDeciders);
        Assert.True(decision.Accept);
    }

    internal sealed class FixedDecider(ChannelOpenDecision decision) : IChannelOpenDecider
    {
        public int Calls { get; private set; }

        public Task<ChannelOpenDecision> DecideAsync(ChannelOpenRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(decision);
        }
    }

    private sealed class ThrowingDecider : IChannelOpenDecider
    {
        public Task<ChannelOpenDecision> DecideAsync(ChannelOpenRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("broken");
    }
}