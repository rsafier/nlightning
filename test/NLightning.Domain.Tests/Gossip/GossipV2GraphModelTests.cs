namespace NLightning.Domain.Tests.Gossip;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Addresses;
using Domain.Gossip.Graph;
using Domain.Gossip.Queries;
using Domain.Protocol.Constants;
using Domain.Protocol.GossipV2;
using Domain.Protocol.Payloads;

/// <summary>
/// The graph read model of taproot gossip (BOLTs PR #1059, NL-878): v2 policies next to v1 ones, block-height
/// staleness, the <c>channel_update_2</c> defaults, nodes from <c>node_announcement_2</c>, and the
/// <c>block_height_range</c> of <c>gossip_timestamp_filter</c>.
/// </summary>
public class GossipV2GraphModelTests
{
    private static readonly ShortChannelId s_scid = new(900, 1, 0);

    private static CompactPubKey Key(byte last)
    {
        var bytes = new byte[33];
        bytes[0] = 0x02;
        bytes[32] = last;
        return new CompactPubKey(bytes);
    }

    private static ChannelUpdate2Payload Update2(byte direction = 0, uint blockHeight = 1_000, byte disableFlags = 0,
                                                 uint inboundBase = 0, uint inboundPpm = 0) =>
        ChannelUpdate2Payload.Create(ChainConstants.Regtest, s_scid, direction, blockHeight, disableFlags, 40, 1_000,
                                     400_000_000, 1_000, 100, inboundBase, inboundPpm);

    private static GraphChannel Channel() => new(s_scid, Key(1), Key(2), Key(3), Key(4), 1_000_000);

    [Fact]
    public void Given_AnUpdate2WithoutHtlcMaximum_When_MadeAPolicy_Then_ItIsHalfTheCapacity()
    {
        // Arrange: the record is absent (the draft's default floor(capacity / 2))
        var full = Update2();
        var withoutMaximum = ChannelUpdate2Payload.FromStream(
            new PureTlvStream(full.Stream.Records.Where(r => r.Type != GossipV2Constants.ChannelUpdate2.HtlcMaximumMsat)));

        // Act
        var policy = GraphPolicy.FromChannelUpdate2(withoutMaximum, 1_000_001_000);
        var unknownCapacity = GraphPolicy.FromChannelUpdate2(withoutMaximum, null);

        // Assert
        Assert.Null(withoutMaximum.HtlcMaximumMsat);
        Assert.Equal(500_000_500UL, policy.HtlcMaximumMsat);
        Assert.Equal(0UL, unknownCapacity.HtlcMaximumMsat);
    }

    [Fact]
    public void Given_AnUpdate2_When_MadeAPolicy_Then_ItIsAV2PolicyDatedByItsBlockHeight()
    {
        // Act
        var policy = GraphPolicy.FromChannelUpdate2(Update2(direction: 1, blockHeight: 777, disableFlags: 0b100,
                                                            inboundBase: 5, inboundPpm: 6), 1_000_000_000);

        // Assert
        Assert.True(policy.IsV2);
        Assert.Equal(2, policy.GossipVersion);
        Assert.Equal(777u, policy.Timestamp);
        Assert.Equal(1, policy.Direction);
        Assert.True(policy.IsDisabled);
        Assert.Equal(0b100, policy.DisableFlags);
        Assert.Equal(5u, policy.InboundFeeBaseMsat);
        Assert.Equal(6u, policy.InboundFeeProportionalMillionths);
        Assert.True(policy.HasInboundFee);
        Assert.Equal(400_000_000UL, policy.HtlcMaximumMsat);
    }

    [Fact]
    public void Given_V1AndV2Policies_When_Read_Then_EachHasItsSlotAndRoutingPrefersV2()
    {
        // Arrange
        var v1 = new GraphPolicy(1_700_000_000, 1, 0, 40, 1, 100, 10, 10);
        var v2 = GraphPolicy.FromChannelUpdate2(Update2(), 1_000_000_000);

        // Act
        var onlyV1 = Channel().WithPolicy(v1);
        var both = onlyV1.WithPolicy(v2);

        // Assert
        Assert.Same(v1, onlyV1.GetRoutingPolicy(0));
        Assert.Same(v1, both.GetPolicy(0));
        Assert.Same(v1, both.GetPolicy(0, 1));
        Assert.Same(v2, both.GetPolicy(0, 2));
        Assert.Same(v2, both.GetRoutingPolicy(0));
        Assert.Same(v2, both.Policy1V2);
        Assert.Null(both.GetRoutingPolicy(1));
        Assert.NotEqual(onlyV1, both);
    }

    [Theory]
    [InlineData(3_000u, 900u, true)]
    [InlineData(3_000u, 984u, false)]
    [InlineData(null, 1u, false)]
    public void Given_AV2OnlyChannel_When_CheckedForStaleness_Then_ItsBlockHeightCountsAgainstTheTip(
        uint? tip, uint updateHeight, bool stale)
    {
        // Arrange: stale below tip - 2016 (max_backdate_blocks); never with an unknown tip
        var channel = Channel() with { Versions = GraphGossipVersions.V2 };
        channel = channel.WithPolicy(GraphPolicy.FromChannelUpdate2(Update2(blockHeight: updateHeight), null));

        // Act
        var result = channel.IsStale(nowUnixSeconds: 1_700_000_000, TimeSpan.FromDays(14), tip);

        // Assert
        Assert.Equal(stale, result);
    }

    [Fact]
    public void Given_AStaleV1PolicyAndAFreshV2One_When_CheckedForStaleness_Then_TheRoutingPolicyCounts()
    {
        // Arrange: the v1 update is a year old, the v2 one fresh
        var channel = Channel().WithPolicy(new GraphPolicy(1_600_000_000, 1, 0, 40, 1, 100, 10, 10))
                               .WithPolicy(GraphPolicy.FromChannelUpdate2(Update2(blockHeight: 2_990), null));

        // Act
        var stale = channel.IsStale(1_700_000_000, TimeSpan.FromDays(14), 3_000);

        // Assert
        Assert.False(stale);
    }

    [Fact]
    public void Given_ANodeAnnouncement2_When_MadeAGraphNode_Then_ItsAliasIsPaddedAndItIsV2Only()
    {
        // Arrange
        var address = AddressDescriptorCodec.DecodeList([1, 127, 0, 0, 1, 0x26, 0x07]).Addresses;
        var announcement = NodeAnnouncement2Payload.Create([0x02], 1_234, Key(9), ReadOnlySpan<byte>.Empty,
                                                           "abc"u8, address);

        // Act
        var node = GraphNode.FromNodeAnnouncement2(announcement, announcement.GetBytes());

        // Assert
        Assert.Equal(GraphGossipVersions.V2, node.Versions);
        Assert.False(node.HasV1);
        Assert.Equal(1_234u, node.BlockHeight);
        Assert.Equal(0u, node.Timestamp);
        Assert.Equal("abc", node.AliasText);
        Assert.Equal(GraphNode.AliasLength, node.Alias.Length);
        Assert.Equal("#000000", node.ColorHex);
        Assert.Single(node.Addresses);
        Assert.Equal(announcement.GetBytes(), node.RawAnnouncement2.ToArray());
        Assert.True(node.RawAnnouncement.IsEmpty);
    }

    [Theory]
    [InlineData(100u, 0u, new byte[] { 0, 0, 0, 100 })]
    [InlineData(100u, 255u, new byte[] { 0, 0, 0, 100, 0xFF })]
    [InlineData(0xFFFFFFFFu, 0xFFFFFFFFu, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF })]
    [InlineData(7u, 0x100u, new byte[] { 0, 0, 0, 7, 1, 0 })]
    public void Given_ABlockHeightRange_When_Encoded_Then_TheTu32IsMinimalAndDecodesBack(uint first, uint num,
                                                                                      byte[] expected)
    {
        // Arrange
        var range = new GossipBlockHeightRange(first, num);

        // Act
        var encoded = range.Encode();
        var decoded = GossipBlockHeightRange.TryDecode(encoded, out var back);

        // Assert
        Assert.Equal(expected, encoded);
        Assert.True(decoded);
        Assert.Equal(range, back);
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 1 })]
    [InlineData(new byte[] { 0, 0, 0, 1, 0, 5 })]
    [InlineData(new byte[] { 0, 0, 0, 1, 1, 2, 3, 4, 5 })]
    public void Given_AMalformedBlockHeightRange_When_Decoded_Then_Refused(byte[] value)
    {
        // Act
        var decoded = GossipBlockHeightRange.TryDecode(value, out _);

        // Assert
        Assert.False(decoded);
    }

    [Fact]
    public void Given_ABlockHeightRange_When_Asked_Then_ItIncludesFirstAndExcludesTheEnd()
    {
        // Arrange
        var range = new GossipBlockHeightRange(100, 10);

        // Act & Assert
        Assert.True(range.Includes(100));
        Assert.True(range.Includes(109));
        Assert.False(range.Includes(110));
        Assert.False(range.Includes(99));
        Assert.True(new GossipBlockHeightRange(5, uint.MaxValue).Includes(uint.MaxValue - 1));
        Assert.False(GossipBlockHeightRange.None.Includes(uint.MaxValue));
    }

    [Fact]
    public void Given_ANullableBitcoinKey_When_AChannelHasNone_Then_ItIsKeptAsNull()
    {
        // Arrange & Act: a channel_announcement_2 of the 3-key proof carries no bitcoin keys
        var channel = new GraphChannel(s_scid, Key(1), Key(2), null, null, 1_000) { Versions = GraphGossipVersions.V2 };

        // Assert
        Assert.Null(channel.BitcoinKey1);
        Assert.Null(channel.BitcoinKey2);
        Assert.True(channel.HasV2);
        Assert.False(channel.HasV1);
    }
}