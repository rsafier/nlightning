using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Payments.Policy;

using Application.Channels.RoutingPolicies;
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
    [Theory]
    // Inside a blinded route the recipient's payment_relay is our fee: at least our policy forwards, even when its
    // rounding leaves the computed amount a msat short of the BOLT 7 fee
    [InlineData(1_000U, 100U, true)]
    [InlineData(2_000U, 100U, true)]
    [InlineData(999U, 100U, false)]
    [InlineData(1_000U, 99U, false)]
    public void Given_BlindedRelay_When_Evaluated_Then_ComparedWithOurPolicyInsteadOfTheAmounts(uint feeBase,
        uint feeRate, bool forwards)
    {
        // Arrange: an incoming amount one msat below the BOLT 7 fee
        var relay = new Domain.Protocol.Onion.Models.BlindedPaymentRelay(Delta, feeRate, feeBase);
        var request = Request(incomingMsat: AmountMsat + Fee(AmountMsat) - 1) with { BlindedRelay = relay };

        // Act
        var decision = CreatePolicy().Evaluate(request);

        // Assert
        Assert.Equal(forwards, decision.IsForward);
        if (!forwards)
            Assert.Equal(FailureCode.FeeInsufficient, decision.FailureCode);
    }

    [Fact]
    public void Given_BlindedRelayWithASmallerDelta_When_Evaluated_Then_IncorrectCltvExpiry()
    {
        // Arrange: outgoing = incoming - relay delta (39) is one block short of our delta (40)
        var relay = new Domain.Protocol.Onion.Models.BlindedPaymentRelay(Delta - 1, 100, 1_000);
        var request = Request(incomingCltv: OutgoingCltv + Delta - 1) with { BlindedRelay = relay };

        // Act
        var decision = CreatePolicy().Evaluate(request);

        // Assert
        Assert.Equal(FailureCode.IncorrectCltvExpiry, decision.FailureCode);
    }

    // Wave sp1 lane SP1-G: the outgoing channel's own policy (setchannelpolicy) replaces Node:Routing's values

    private static readonly ChannelId s_policyChannelId = new(Enumerable.Repeat((byte)0x42, 32).ToArray());

    private HtlcForwardingPolicy CreatePolicy(ConfiguredChannelPolicy channelPolicy)
    {
        var provider = new Mock<IChannelPolicyProvider>();
        provider.Setup(p => p.GetConfiguredPolicy(It.IsAny<ChannelId>()))
                .Returns((ChannelId id) => id == s_policyChannelId
                                               ? channelPolicy
                                               : ConfiguredChannelPolicy.From(_nodeOptions.Routing, null));
        return new HtlcForwardingPolicy(Options.Create(_nodeOptions), provider.Object);
    }

    private static OutgoingChannelInfo PolicyChannel() =>
        new(s_policyChannelId, true, LightningMoney.MilliSatoshis(1), LightningMoney.MilliSatoshis(10_000_000_000));

    [Theory]
    // The channel charges 5,000 msat + 2,000 ppm: 1,000,000 msat forwarded costs 7,000 msat
    [InlineData(7_000UL, true)]
    [InlineData(6_999UL, false)]
    // Node:Routing's fee (1,100 msat) is no longer enough on that channel
    [InlineData(1_100UL, false)]
    public void Given_AChannelFee_When_Evaluated_Then_TheOutgoingChannelsFeeIsRequired(ulong feeMsat, bool forwards)
    {
        // Arrange
        var policy = CreatePolicy(new ConfiguredChannelPolicy(5_000, 2_000, Delta, 1_000, null));
        var request = Request(AmountMsat + feeMsat, channel: PolicyChannel());

        // Act
        var decision = policy.Evaluate(request);

        // Assert
        Assert.Equal(forwards, decision.IsForward);
        if (!forwards)
            Assert.Equal(FailureCode.FeeInsufficient, decision.FailureCode);
    }

    [Theory]
    [InlineData(80, true)]
    [InlineData(79, false)]
    public void Given_AChannelCltvDelta_When_Evaluated_Then_TheOutgoingChannelsDeltaIsRequired(uint delta,
        bool forwards)
    {
        // Arrange
        var policy = CreatePolicy(new ConfiguredChannelPolicy(1_000, 100, 80, 1_000, null));
        var request = Request(incomingCltv: OutgoingCltv + delta, channel: PolicyChannel());

        // Act
        var decision = policy.Evaluate(request);

        // Assert
        Assert.Equal(forwards, decision.IsForward);
        if (!forwards)
            Assert.Equal(FailureCode.IncorrectCltvExpiry, decision.FailureCode);
    }

    [Theory]
    [InlineData(500_000UL, true)]
    [InlineData(500_001UL, false)]
    public void Given_AChannelHtlcMaximum_When_Evaluated_Then_AboveItFailsWithTemporaryChannelFailure(
        ulong amountMsat, bool forwards)
    {
        // Arrange
        var policy = CreatePolicy(new ConfiguredChannelPolicy(1_000, 100, Delta, 1_000, 500_000));
        var request = Request(amountMsat: amountMsat, channel: PolicyChannel());

        // Act
        var decision = policy.Evaluate(request);

        // Assert
        Assert.Equal(forwards, decision.IsForward);
        if (!forwards)
            Assert.Equal(FailureCode.TemporaryChannelFailure, decision.FailureCode);
    }

    [Theory]
    [InlineData(200_000UL, true)]
    [InlineData(199_999UL, false)]
    public void Given_AChannelHtlcMinimum_When_Evaluated_Then_BelowItFailsWithAmountBelowMinimum(ulong amountMsat,
        bool forwards)
    {
        // Arrange
        var policy = CreatePolicy(new ConfiguredChannelPolicy(1_000, 100, Delta, 200_000, null));
        var request = Request(amountMsat: amountMsat, channel: PolicyChannel());

        // Act
        var decision = policy.Evaluate(request);

        // Assert
        Assert.Equal(forwards, decision.IsForward);
        if (!forwards)
            Assert.Equal(FailureCode.AmountBelowMinimum, decision.FailureCode);
    }

    [Fact]
    public void Given_AnOverrideOnAnotherChannel_When_Evaluated_Then_NodeRoutingApplies()
    {
        // Arrange: the override (max 1 msat) is for s_policyChannelId, the HTLC leaves on ChannelId.Zero
        var policy = CreatePolicy(new ConfiguredChannelPolicy(1_000_000, 1_000_000, 1_000, 1, 1));

        // Act
        var decision = policy.Evaluate(Request());

        // Assert
        Assert.True(decision.IsForward);
    }

    [Fact]
    public void Given_ABlindedRelayBelowTheChannelsFee_When_Evaluated_Then_FeeInsufficient()
    {
        // Arrange: the relay pays Node:Routing's fee (1,000 msat + 100 ppm), the channel asks for 2,000 msat
        var policy = CreatePolicy(new ConfiguredChannelPolicy(2_000, 100, Delta, 1_000, null));
        var relay = new Domain.Protocol.Onion.Models.BlindedPaymentRelay(Delta, 100, 1_000);
        var request = Request(channel: PolicyChannel()) with { BlindedRelay = relay };

        // Act
        var decision = policy.Evaluate(request);

        // Assert
        Assert.Equal(FailureCode.FeeInsufficient, decision.FailureCode);
    }
}