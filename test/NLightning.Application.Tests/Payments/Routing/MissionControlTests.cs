using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Payments.Routing;

using Application.Payments.Routing;
using Application.Payments.Send;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Models;
using Domain.Money;
using Domain.Protocol.Onion.Enums;
using Domain.Routing.Pathfinding;
using Switch;

/// <summary>
/// BOLT 7 plan G4-T2: what mission control learns from our payments' outcomes (lower bounds from what went through,
/// upper bounds from <c>temporary_channel_failure</c>, unusable channels, penalized nodes), how it fades and that it
/// hands out copies. The route is us → Carol → David → Erin.
/// </summary>
public class MissionControlTests
{
    private static readonly CompactPubKey s_carol = new TestNodeKeyManager(0x03).NodeId;
    private static readonly CompactPubKey s_david = new TestNodeKeyManager(0x04).NodeId;
    private static readonly CompactPubKey s_erin = new TestNodeKeyManager(0x05).NodeId;
    private static readonly ShortChannelId s_scidCd = new(101, 2, 1);
    private static readonly ShortChannelId s_scidDe = new(102, 3, 0);

    private readonly SteppedTimeProvider _time = new();

    private MissionControl Create(TimeSpan? halfLife = null, TimeSpan? nodePenalty = null) =>
        new(Options.Create(new PaymentSendOptions
        {
            MissionControlHalfLife = halfLife ?? TimeSpan.FromHours(1),
            NodeFailurePenalty = nodePenalty ?? TimeSpan.FromHours(1)
        }), _time);

    /// <summary>Carol and David each forward 1,000,000 msat (David charges nothing).</summary>
    private static PaymentRoute Route()
    {
        var target = new PaymentTarget(s_erin, Enumerable.Repeat((byte)0x11, 32).ToArray(),
                                       Enumerable.Repeat((byte)0x22, 32).ToArray(), null, 40, []);
        return HintRouteBuilder.BuildAlong([
                                               new RoutingInfo(s_carol, s_scidCd, 2_000, 500, 40),
                                               new RoutingInfo(s_david, s_scidDe, 0, 0, 40)
                                           ], target, LightningMoney.MilliSatoshis(1_000_000), 800);
    }

    private double Probability(MissionControl missionControl, ShortChannelId scid, CompactPubKey from,
                               CompactPubKey to, ulong amount)
    {
        var snapshot = missionControl.GetSnapshot();
        return snapshot.Liquidity.GetSuccessProbability(DirectedChannel.Between(scid, from, to), amount, 10_000_000,
                                                        (ulong)_time.GetUtcNow().ToUnixTimeSeconds(), 0.6);
    }

    [Fact]
    public void Given_ASucceededRoute_When_Recorded_Then_EveryChannelAfterOursGetsALowerBound()
    {
        // Arrange
        var missionControl = Create();
        var route = Route();

        // Act
        missionControl.RecordSuccess(route);

        // Assert: Carol → David carried 1,000,000 msat, David → Erin too (no fee after David)
        Assert.True(missionControl.TryGetBounds(s_scidCd, s_carol, s_david, out var minCd, out var maxCd));
        Assert.Equal((1_000_000UL, (ulong?)null), (minCd, maxCd));
        Assert.True(missionControl.TryGetBounds(s_scidDe, s_david, s_erin, out var minDe, out _));
        Assert.Equal(1_000_000UL, minDe);

        // The bounds' probability is exact given the decay weight: below the fresh lower bound it is the prior-free
        // 1.0, a hair under at a 1 h half-life (the unix-seconds age of the seconds the test itself spent, NL-434);
        // one half-life later it has faded exactly halfway to the 0.6 prior
        Assert.Equal(1.0, Probability(missionControl, s_scidCd, s_carol, s_david, 999_999), 2);
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0.8, Probability(missionControl, s_scidCd, s_carol, s_david, 999_999), 2);
        Assert.Equal(2, missionControl.ChannelRecordCount);
    }

    [Fact]
    public void Given_TemporaryChannelFailureAtDavid_When_Recorded_Then_DavidErinIsBoundedAndCarolDavidCarried()
    {
        // Arrange
        var missionControl = Create();

        // Act
        missionControl.RecordFailure(Route(), 1, FailureCode.TemporaryChannelFailure);

        // Assert
        Assert.True(missionControl.TryGetBounds(s_scidDe, s_david, s_erin, out _, out var max));
        Assert.Equal(1_000_000UL, max);

        // The bounds' probability is the exact uniform formula on [0, 1,000,000) once decayed (weight w: half of
        // the interval is 0.5·w + (1−w)·0.6, the refused amount only the complement (1−w)·0.6 above 0); fresh the
        // weight is a hair under 1 (the unix-seconds age of the test's own seconds, NL-434: the old `> 0` proved
        // nothing and an exact 0 flaked on a second boundary under load), and one half-life later both have faded
        // exactly halfway to the 0.6 prior
        var half = Probability(missionControl, s_scidDe, s_david, s_erin, 500_000);
        Assert.Equal(0.5, half, 2);
        Assert.True(Probability(missionControl, s_scidDe, s_david, s_erin, 1_000_000) < half);
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0.55, Probability(missionControl, s_scidDe, s_david, s_erin, 500_000), 2);
        Assert.Equal(0.3, Probability(missionControl, s_scidDe, s_david, s_erin, 1_000_000), 2);

        Assert.True(missionControl.TryGetBounds(s_scidCd, s_carol, s_david, out var carried, out _));
        Assert.Equal(1_000_000UL, carried);
    }

    [Theory]
    [InlineData(FailureCode.UnknownNextPeer)]
    [InlineData(FailureCode.PermanentChannelFailure)]
    [InlineData(FailureCode.ChannelDisabled)]
    public void Given_AChannelFailure_When_Recorded_Then_TheChannelIsUnusableUntilItFades(FailureCode code)
    {
        // Arrange
        var missionControl = Create(TimeSpan.FromMinutes(10));

        // Act
        missionControl.RecordFailure(Route(), 0, code);

        // Assert: at a 10 minutes half-life the fresh bound weighs almost everything, so the unusable channel sits
        // near 0 and is back at exactly halfway to the prior after one half-life and at the prior after many
        // (NL-434: the exact fresh read flaked on the unix-seconds boundary under a loaded run)
        Assert.True(Probability(missionControl, s_scidCd, s_carol, s_david, 1) < 0.05);
        _time.Advance(TimeSpan.FromMinutes(10));
        // A loose digit on purpose: the weight of the test's own real seconds may fall a little below 0.5
        Assert.Equal(0.3, Probability(missionControl, s_scidCd, s_carol, s_david, 1), 1);
        _time.Advance(TimeSpan.FromHours(10));
        Assert.Equal(0.6, Probability(missionControl, s_scidCd, s_carol, s_david, 1), 3);
    }

    [Theory]
    [InlineData(FailureCode.FeeInsufficient)]
    [InlineData(FailureCode.IncorrectCltvExpiry)]
    [InlineData(FailureCode.AmountBelowMinimum)]
    [InlineData(FailureCode.ExpiryTooSoon)]
    public void Given_APolicyFailure_When_Recorded_Then_NoLiquidityIsLearntForTheChannel(FailureCode code)
    {
        // Arrange
        var missionControl = Create();

        // Act
        missionControl.RecordFailure(Route(), 1, code);

        // Assert
        Assert.False(missionControl.TryGetBounds(s_scidDe, s_david, s_erin, out _, out _));
        Assert.Empty(missionControl.GetSnapshot().PenalizedNodes);
    }

    [Fact]
    public void Given_ANodeFailure_When_Recorded_Then_TheNodeIsPenalizedForThePenaltyDuration()
    {
        // Arrange
        var missionControl = Create(nodePenalty: TimeSpan.FromMinutes(30));

        // Act
        missionControl.RecordFailure(Route(), 1, FailureCode.TemporaryNodeFailure);

        // Assert
        Assert.Contains(s_david, missionControl.GetSnapshot().PenalizedNodes);
        _time.Advance(TimeSpan.FromMinutes(31));
        Assert.Empty(missionControl.GetSnapshot().PenalizedNodes);
    }

    [Fact]
    public void Given_ANodeFailureFromOurPeer_When_Recorded_Then_ItIsNotPenalized()
    {
        // Arrange
        var missionControl = Create();

        // Act: hop 0 is our own peer (Carol)
        missionControl.RecordFailure(Route(), 0, FailureCode.TemporaryNodeFailure);

        // Assert
        Assert.Empty(missionControl.GetSnapshot().PenalizedNodes);
    }

    [Fact]
    public void Given_AFailureFromThePayee_When_Recorded_Then_OnlyTheCarriedAmountsAreLearnt()
    {
        // Arrange
        var missionControl = Create();

        // Act
        missionControl.RecordFailure(Route(), 2, FailureCode.IncorrectOrUnknownPaymentDetails);

        // Assert
        Assert.True(missionControl.TryGetBounds(s_scidDe, s_david, s_erin, out var min, out var max));
        Assert.Equal((1_000_000UL, (ulong?)null), (min, max));
        Assert.Empty(missionControl.GetSnapshot().PenalizedNodes);
    }

    [Fact]
    public void Given_ASnapshot_When_MissionControlLearnsMore_Then_TheSnapshotDoesNotChange()
    {
        // Arrange
        var missionControl = Create();
        var snapshot = missionControl.GetSnapshot();

        // Act
        missionControl.RecordFailure(Route(), 0, FailureCode.UnknownNextPeer);

        // Assert
        Assert.Equal(0, snapshot.Liquidity.Count);
        Assert.Equal(1, missionControl.GetSnapshot().Liquidity.Count);
    }
}