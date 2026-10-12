namespace NLightning.Application.Tests.Channels.RoutingPolicies;

using Application.Channels.RoutingPolicies;
using Domain.Channels.RoutingPolicies;
using Domain.Money;

/// <summary>
/// Wave sp1 lane SP1-G: what a channel announces under its override and which overrides BOLT 7 allows.
/// </summary>
public class ChannelPolicyRulesTests
{
    private readonly Domain.Node.Options.NodeOptions _nodeOptions = ChannelPolicyTestKit.CreateNodeOptions();

    [Fact]
    public void Given_NoOverride_When_Resolved_Then_NodeRoutingAndTheChannelsLimitsApply()
    {
        // Arrange: the peer's minimum (5,000) is above ours (1,000); its max in flight (800M) below the capacity (1G)
        var channel = ChannelPolicyTestKit.CreateChannel();

        // Act
        var policy = ChannelPolicyRules.Resolve(channel, _nodeOptions.Routing, null);

        // Assert
        Assert.Equal(channel.ChannelId, policy.ChannelId);
        Assert.Equal(1_000u, policy.FeeBaseMsat);
        Assert.Equal(1u, policy.FeeProportionalMillionths);
        Assert.Equal((ushort)40, policy.CltvExpiryDelta);
        Assert.Equal(5_000ul, policy.HtlcMinimumMsat);
        Assert.Equal(800_000_000ul, policy.HtlcMaximumMsat);
        Assert.Null(policy.Override);
        Assert.False(policy.IsFeeBaseMsatOverridden);
    }

    [Fact]
    public void Given_AFullOverride_When_Resolved_Then_EveryValueIsTheChannels()
    {
        // Arrange
        var channel = ChannelPolicyTestKit.CreateChannel();
        var policyOverride = new ChannelPolicyOverride(channel.ChannelId, 7, 250, 144, 20_000, 300_000_000);

        // Act
        var policy = ChannelPolicyRules.Resolve(channel, _nodeOptions.Routing, policyOverride);

        // Assert
        Assert.Equal(7u, policy.FeeBaseMsat);
        Assert.Equal(250u, policy.FeeProportionalMillionths);
        Assert.Equal((ushort)144, policy.CltvExpiryDelta);
        Assert.Equal(20_000ul, policy.HtlcMinimumMsat);
        Assert.Equal(300_000_000ul, policy.HtlcMaximumMsat);
        Assert.True(policy.IsFeeBaseMsatOverridden && policy.IsFeeProportionalMillionthsOverridden
                 && policy.IsCltvExpiryDeltaOverridden && policy.IsHtlcMinimumMsatOverridden
                 && policy.IsHtlcMaximumMsatOverridden);
    }

    [Fact]
    public void Given_APartialOverride_When_Resolved_Then_UnsetValuesFallBackToNodeRouting()
    {
        // Arrange
        var channel = ChannelPolicyTestKit.CreateChannel();
        var policyOverride = new ChannelPolicyOverride(channel.ChannelId, FeeProportionalMillionths: 900);

        // Act
        var policy = ChannelPolicyRules.Resolve(channel, _nodeOptions.Routing, policyOverride);

        // Assert
        Assert.Equal(1_000u, policy.FeeBaseMsat);
        Assert.Equal(900u, policy.FeeProportionalMillionths);
        Assert.Equal((ushort)40, policy.CltvExpiryDelta);
        Assert.True(policy.IsFeeProportionalMillionthsOverridden);
        Assert.False(policy.IsFeeBaseMsatOverridden);
        Assert.False(policy.IsHtlcMaximumMsatOverridden);
    }

    [Fact]
    public void Given_AnOverrideMinimumBelowThePeers_When_Resolved_Then_ThePeersMinimumIsAnnounced()
    {
        // Arrange: BOLT 7 htlc_minimum_msat is what the channel peer will accept
        var channel = ChannelPolicyTestKit.CreateChannel(peerHtlcMinimumMsat: 5_000);
        var policyOverride = new ChannelPolicyOverride(channel.ChannelId, HtlcMinimumMsat: 1);

        // Act
        var policy = ChannelPolicyRules.Resolve(channel, _nodeOptions.Routing, policyOverride);

        // Assert
        Assert.Equal(5_000ul, policy.HtlcMinimumMsat);
    }

    [Fact]
    public void Given_AnOverrideMaximumAboveThePeersMaxInFlight_When_Resolved_Then_TheSmallerIsAnnounced()
    {
        // Arrange
        var channel = ChannelPolicyTestKit.CreateChannel(peerMaxInFlightMsat: 100_000_000);
        var policyOverride = new ChannelPolicyOverride(channel.ChannelId, HtlcMaximumMsat: 900_000_000);

        // Act
        var policy = ChannelPolicyRules.Resolve(channel, _nodeOptions.Routing, policyOverride);

        // Assert
        Assert.Equal(100_000_000ul, policy.HtlcMaximumMsat);
    }

    [Theory]
    [InlineData((ushort)34, true)]
    [InlineData((ushort)33, false)]
    [InlineData((ushort)2016, true)]
    [InlineData((ushort)2017, false)]
    public void Given_ACltvDelta_When_Validated_Then_OnlyFrom34ToMaxCltvExpiryDistanceIsAccepted(ushort delta,
        bool valid)
    {
        // Arrange
        var channel = ChannelPolicyTestKit.CreateChannel();

        // Act
        var errors = ChannelPolicyRules.GetValidationErrors(channel, _nodeOptions.Routing,
                                                            new ChannelPolicyOverride(channel.ChannelId,
                                                                CltvExpiryDelta: delta));

        // Assert
        Assert.Equal(valid, errors.Count == 0);
    }

    [Fact]
    public void Given_ACltvDeltaNotAboveExpiryTooSoon_When_Validated_Then_Refused()
    {
        // Arrange: Node:Routing refuses ExpiryTooSoonBlocks >= CltvExpiryDelta; so does the channel policy
        _nodeOptions.Routing.ExpiryTooSoonBlocks = 40;
        var channel = ChannelPolicyTestKit.CreateChannel();

        // Act
        var errors = ChannelPolicyRules.GetValidationErrors(channel, _nodeOptions.Routing,
                                                            new ChannelPolicyOverride(channel.ChannelId,
                                                                CltvExpiryDelta: 40));

        // Assert
        Assert.Contains(errors, e => e.Contains("ExpiryTooSoonBlocks", StringComparison.Ordinal));
    }

    [Theory]
    // capacity 1,000,000 sat = 1,000,000,000 msat; BOLT 7: htlc_maximum_msat MUST NOT exceed it
    [InlineData(1_000_000_000UL, true)]
    [InlineData(1_000_000_001UL, false)]
    [InlineData(0UL, false)]
    public void Given_AnHtlcMaximum_When_Validated_Then_ItMustBePositiveAndAtMostTheCapacity(ulong maximum,
        bool valid)
    {
        // Arrange: the peer allows the whole capacity in flight, so only the capacity bounds the maximum
        var channel = ChannelPolicyTestKit.CreateChannel(peerMaxInFlightMsat: 0);

        // Act
        var errors = ChannelPolicyRules.GetValidationErrors(channel, _nodeOptions.Routing,
                                                            new ChannelPolicyOverride(channel.ChannelId,
                                                                HtlcMaximumMsat: maximum));

        // Assert
        Assert.Equal(valid, errors.Count == 0);
    }

    [Theory]
    [InlineData(10_000UL, 10_000UL, true)]
    [InlineData(10_001UL, 10_000UL, false)]
    // The peer's minimum (5,000) counts too: a maximum below it leaves no HTLC
    [InlineData(1UL, 4_999UL, false)]
    public void Given_MinimumAndMaximum_When_Validated_Then_TheAnnouncedMinimumMustNotExceedTheMaximum(ulong minimum,
        ulong maximum, bool valid)
    {
        // Arrange
        var channel = ChannelPolicyTestKit.CreateChannel(peerHtlcMinimumMsat: 5_000);

        // Act
        var errors = ChannelPolicyRules.GetValidationErrors(channel, _nodeOptions.Routing,
                                                            new ChannelPolicyOverride(channel.ChannelId,
                                                                HtlcMinimumMsat: minimum, HtlcMaximumMsat: maximum));

        // Assert
        Assert.Equal(valid, errors.Count == 0);
    }

    [Fact]
    public void Given_AMinimumAboveTheCapacity_When_Validated_Then_Refused()
    {
        // Arrange
        var channel = ChannelPolicyTestKit.CreateChannel(capacity: LightningMoney.Satoshis(20_000));

        // Act
        var errors = ChannelPolicyRules.GetValidationErrors(channel, _nodeOptions.Routing,
                                                            new ChannelPolicyOverride(channel.ChannelId,
                                                                HtlcMinimumMsat: 20_000_001));

        // Assert
        Assert.Single(errors);
    }

    [Fact]
    public void Given_TheMaximumFeeFields_When_Validated_Then_Accepted()
    {
        // Arrange: fee_base_msat and fee_proportional_millionths are u32 (BOLT 7): every value can be announced
        var channel = ChannelPolicyTestKit.CreateChannel();

        // Act
        var errors = ChannelPolicyRules.GetValidationErrors(channel, _nodeOptions.Routing,
                                                            new ChannelPolicyOverride(channel.ChannelId,
                                                                uint.MaxValue, uint.MaxValue));

        // Assert
        Assert.Empty(errors);
    }

    [Fact]
    public void Given_AnOverrideOfAnotherChannel_When_Validated_Then_Refused()
    {
        // Arrange
        var channel = ChannelPolicyTestKit.CreateChannel();
        var other = ChannelPolicyTestKit.CreateChannel(2);

        // Act
        var errors = ChannelPolicyRules.GetValidationErrors(channel, _nodeOptions.Routing,
                                                            new ChannelPolicyOverride(other.ChannelId, 5));

        // Assert
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Given_APatch_When_Merged_Then_ItsValuesReplaceAndItsNullsKeep()
    {
        // Arrange
        var channel = ChannelPolicyTestKit.CreateChannel();
        var current = new ChannelPolicyOverride(channel.ChannelId, 5, 6, 50, 7, 8);
        var patch = new ChannelPolicyOverride(channel.ChannelId, FeeProportionalMillionths: 60, HtlcMaximumMsat: 80);
        var at = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        // Act
        var merged = ChannelPolicyRules.Merge(current, patch, at);

        // Assert
        Assert.Equal(new ChannelPolicyOverride(channel.ChannelId, 5, 60, 50, 7, 80, at), merged);
        Assert.True(ChannelPolicyRules.HasSameValues(merged, merged with { UpdatedAt = default }));
        Assert.False(ChannelPolicyRules.HasSameValues(current, merged));
        Assert.True(ChannelPolicyRules.IsEmpty(new ChannelPolicyOverride(channel.ChannelId)));
        Assert.True(ChannelPolicyRules.HasSameValues(null, new ChannelPolicyOverride(channel.ChannelId)));
    }
}