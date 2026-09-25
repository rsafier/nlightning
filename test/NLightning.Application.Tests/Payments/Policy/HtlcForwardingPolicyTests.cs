using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Payments.Policy;

using Application.Payments.Policy;
using Domain.Channels.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Models;
using Domain.Protocol.Onion.Enums;

/// <summary>
/// ONION M4-T4: the forwarding policy table (BOLT 4 forwarding-node failures, BOLT 7 fee formula).
/// </summary>
public class HtlcForwardingPolicyTests
{
    private const uint Height = 1_000;
    private const ushort Delta = 40;
    private const ulong AmountMsat = 1_000_000;
    private const uint OutgoingCltv = Height + 100;

    private readonly NodeOptions _nodeOptions = new()
    {
        Routing = new RoutingOptions
        {
            FeeBaseMsat = 1_000,
            FeeProportionalMillionths = 100,
            CltvExpiryDelta = Delta,
            ExpiryTooSoonBlocks = 18,
            MaxCltvExpiryDistance = 2016,
            HtlcMinimumMsat = 1_000
        }
    };

    private HtlcForwardingPolicy CreatePolicy() => new(Options.Create(_nodeOptions));

    // BOLT 7: fee_base_msat + amount_to_forward * fee_proportional_millionths / 1000000, rounded down
    private static ulong Fee(ulong amountMsat) => 1_000 + amountMsat * 100 / 1_000_000;

    private static OutgoingChannelInfo Channel(bool usable = true, ulong htlcMinimumMsat = 1,
                                               ulong availableMsat = 10_000_000_000) =>
        new(ChannelId.Zero, usable, LightningMoney.MilliSatoshis(htlcMinimumMsat),
            LightningMoney.MilliSatoshis(availableMsat));

    private static ForwardingRequest Request(ulong? incomingMsat = null, uint? incomingCltv = null,
                                             ulong amountMsat = AmountMsat, uint outgoingCltv = OutgoingCltv,
                                             OutgoingChannelInfo? channel = null, bool unknownChannel = false) =>
        new(LightningMoney.MilliSatoshis(incomingMsat ?? amountMsat + Fee(amountMsat)),
            incomingCltv ?? outgoingCltv + Delta, LightningMoney.MilliSatoshis(amountMsat), outgoingCltv, Height,
            unknownChannel ? null : channel ?? Channel());

    [Fact]
    public void Given_ExactFeeAndDelta_When_Evaluated_Then_Forward()
    {
        // Act
        var decision = CreatePolicy().Evaluate(Request());

        // Assert
        Assert.True(decision.IsForward);
        Assert.Null(decision.FailureCode);
    }

    [Theory]
    // amount, fee paid relative to the BOLT 7 fee, forwards?
    [InlineData(1_000_000UL, 0, true)]
    [InlineData(1_000_000UL, -1, false)]
    [InlineData(1_000_000UL, 1, true)]
    [InlineData(999_999UL, 0, true)] // 999999 * 100 / 1e6 = 99.9999, rounded down to 99
    [InlineData(999_999UL, -1, false)]
    [InlineData(50_002_123UL, 0, true)]
    [InlineData(50_002_123UL, -1, false)]
    public void Given_FeeAroundBolt7Formula_When_Evaluated_Then_OnlyAFullFeeForwards(ulong amountMsat, int delta,
                                                                                     bool forwards)
    {
        // Arrange
        var incoming = (ulong)((long)(amountMsat + Fee(amountMsat)) + delta);

        // Act
        var decision = CreatePolicy().Evaluate(Request(incoming, amountMsat: amountMsat));

        // Assert
        Assert.Equal(forwards, decision.IsForward);
        if (!forwards)
        {
            Assert.Equal(FailureCode.FeeInsufficient, decision.FailureCode);
            Assert.Equal(incoming, decision.HtlcMsat);
        }
    }

    [Fact]
    public void Given_IncomingBelowAmountToForward_When_Evaluated_Then_FeeInsufficient()
    {
        // Act
        var decision = CreatePolicy().Evaluate(Request(AmountMsat - 1));

        // Assert
        Assert.Equal(FailureCode.FeeInsufficient, decision.FailureCode);
    }

    [Fact]
    public void Given_UnknownOutgoingChannel_When_Evaluated_Then_UnknownNextPeer()
    {
        // Act: the fee is also too low, but the unknown channel is reported first
        var decision = CreatePolicy().Evaluate(Request(AmountMsat, unknownChannel: true));

        // Assert
        Assert.Equal(FailureCode.UnknownNextPeer, decision.FailureCode);
    }

    [Fact]
    public void Given_UnusableChannel_When_Evaluated_Then_TemporaryChannelFailure()
    {
        // Act
        var decision = CreatePolicy().Evaluate(Request(channel: Channel(usable: false)));

        // Assert
        Assert.Equal(FailureCode.TemporaryChannelFailure, decision.FailureCode);
    }

    [Theory]
    [InlineData(5_000UL, 4_999UL, false)] // below the channel's htlc_minimum_msat
    [InlineData(5_000UL, 5_000UL, true)]
    [InlineData(1UL, 999UL, false)] // below our RoutingOptions.HtlcMinimumMsat (1000)
    [InlineData(1UL, 1_000UL, true)]
    public void Given_AmountAroundHtlcMinimum_When_Evaluated_Then_BelowIsAmountBelowMinimumWithOutgoingAmount(
        ulong channelMinimumMsat, ulong amountMsat, bool forwards)
    {
        // Act
        var decision = CreatePolicy().Evaluate(Request(amountMsat: amountMsat,
                                                       channel: Channel(htlcMinimumMsat: channelMinimumMsat)));

        // Assert
        Assert.Equal(forwards, decision.IsForward);
        if (!forwards)
        {
            Assert.Equal(FailureCode.AmountBelowMinimum, decision.FailureCode);
            Assert.Equal(amountMsat, decision.HtlcMsat);
        }
    }

    [Theory]
    [InlineData(Delta - 1, false)]
    [InlineData(Delta, true)]
    [InlineData(Delta + 1, true)]
    public void Given_CltvDelta_When_Evaluated_Then_BelowOurDeltaIsIncorrectCltvExpiryWithOutgoingCltv(int delta,
        bool forwards)
    {
        // Act
        var decision = CreatePolicy().Evaluate(Request(incomingCltv: (uint)(OutgoingCltv + delta)));

        // Assert
        Assert.Equal(forwards, decision.IsForward);
        if (!forwards)
        {
            Assert.Equal(FailureCode.IncorrectCltvExpiry, decision.FailureCode);
            Assert.Equal(OutgoingCltv, decision.CltvExpiry);
        }
    }

    [Fact]
    public void Given_IncomingCltvBelowOutgoing_When_Evaluated_Then_IncorrectCltvExpiryWithoutUnderflow()
    {
        // Act
        var decision = CreatePolicy().Evaluate(Request(incomingCltv: 10, outgoingCltv: OutgoingCltv));

        // Assert
        Assert.Equal(FailureCode.IncorrectCltvExpiry, decision.FailureCode);
    }

    [Theory]
    [InlineData(Height + 18, false)]
    [InlineData(Height + 19, true)]
    [InlineData(Height - 1, false)]
    public void Given_OutgoingCltvNearHeight_When_Evaluated_Then_WithinExpiryTooSoonBlocksIsExpiryTooSoon(
        uint outgoingCltv, bool forwards)
    {
        // Act
        var decision = CreatePolicy().Evaluate(Request(outgoingCltv: outgoingCltv));

        // Assert
        Assert.Equal(forwards, decision.IsForward);
        if (!forwards)
            Assert.Equal(FailureCode.ExpiryTooSoon, decision.FailureCode);
    }

    [Theory]
    [InlineData(Height + 2016, true)]
    [InlineData(Height + 2017, false)]
    public void Given_IncomingCltvFarAhead_When_Evaluated_Then_BeyondMaxCltvExpiryDistanceIsExpiryTooFar(
        uint incomingCltv, bool forwards)
    {
        // Act
        var decision = CreatePolicy().Evaluate(Request(incomingCltv: incomingCltv,
                                                       outgoingCltv: incomingCltv - Delta));

        // Assert
        Assert.Equal(forwards, decision.IsForward);
        if (!forwards)
        {
            Assert.Equal(FailureCode.ExpiryTooFar, decision.FailureCode);
            Assert.Null(decision.HtlcMsat);
            Assert.Null(decision.CltvExpiry);
        }
    }

    [Theory]
    [InlineData(AmountMsat - 1, false)]
    [InlineData(AmountMsat, true)]
    public void Given_ChannelLiquidity_When_Evaluated_Then_InsufficientLiquidityIsTemporaryChannelFailure(
        ulong availableMsat, bool forwards)
    {
        // Act
        var decision = CreatePolicy().Evaluate(Request(channel: Channel(availableMsat: availableMsat)));

        // Assert
        Assert.Equal(forwards, decision.IsForward);
        if (!forwards)
            Assert.Equal(FailureCode.TemporaryChannelFailure, decision.FailureCode);
    }

    [Fact]
    public void Given_AmountAboveOurHtlcMaximum_When_Evaluated_Then_TemporaryChannelFailure()
    {
        // Arrange
        _nodeOptions.Routing.HtlcMaximumMsat = AmountMsat - 1;

        // Act
        var decision = CreatePolicy().Evaluate(Request());

        // Assert
        Assert.Equal(FailureCode.TemporaryChannelFailure, decision.FailureCode);
    }

    [Fact]
    public void Given_OptionsChangedAfterConstruction_When_Evaluated_Then_NewFeeApplies()
    {
        // Arrange
        var policy = CreatePolicy();
        _nodeOptions.Routing.FeeBaseMsat = 2_000;

        // Act
        var decision = policy.Evaluate(Request());

        // Assert
        Assert.Equal(FailureCode.FeeInsufficient, decision.FailureCode);
    }
}