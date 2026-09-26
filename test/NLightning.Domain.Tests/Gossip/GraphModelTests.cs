namespace NLightning.Domain.Tests.Gossip;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip;
using Domain.Gossip.Addresses;
using Domain.Gossip.Graph;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Tests.Routing;

public class GraphModelTests
{
    private static readonly CompactPubKey s_node1 = GraphTestKit.NodeId(1);
    private static readonly CompactPubKey s_node2 = GraphTestKit.NodeId(2);
    private static readonly ShortChannelId s_scid = new(700_000, 3, 1);

    [Fact]
    public void Given_NodeIdsOutOfOrder_When_CreatingChannel_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => new GraphChannel(s_scid, s_node2, s_node1, s_node2, s_node1, 1));
        Assert.Throws<ArgumentException>(() => new GraphChannel(s_scid, s_node1, s_node1, s_node1, s_node1, 1));
    }

    [Fact]
    public void Given_Channel_When_AddingPolicies_Then_EachGoesToItsDirection()
    {
        // Arrange
        var channel = new GraphChannel(s_scid, s_node1, s_node2, s_node1, s_node2, 1_000);
        var p1 = GraphTestKit.Policy(1);
        var p2 = GraphTestKit.WithDirection(GraphTestKit.Policy(2), 1);

        // Act
        var updated = channel.WithPolicy(p1).WithPolicy(p2);

        // Assert
        Assert.Null(channel.Policy1);
        Assert.Equal(1u, updated.Policy1!.FeeBaseMsat);
        Assert.Equal(2u, updated.Policy2!.FeeBaseMsat);
        Assert.Equal(0, updated.GetDirectionFrom(s_node1));
        Assert.Equal(1, updated.GetDirectionFrom(s_node2));
        Assert.Equal(s_node2, updated.GetOtherNode(s_node1));
        Assert.Same(updated.Policy2, updated.GetPolicy(1));
        Assert.Equal(1_000_000UL, updated.CapacityMsat);
        Assert.Throws<ArgumentException>(() => updated.GetDirectionFrom(GraphTestKit.NodeId(3)));
    }

    [Theory]
    [InlineData(null, null, true)] // no policy at all
    [InlineData(1_000u, null, false)] // one fresh direction
    [InlineData(1_000u, 100u, true)] // the older one is stale
    [InlineData(1_000u, 1_000u, false)]
    public void Given_PolicyAges_When_CheckingStale_Then_TheOlderPolicyDecides(uint? ts1, uint? ts2, bool stale)
    {
        // Arrange: now = 1,000 + 14 days; a policy at 100 is 900 s older than the limit
        var channel = new GraphChannel(s_scid, s_node1, s_node2, s_node1, s_node2, 1_000);
        if (ts1 is { } t1)
            channel = channel.WithPolicy(GraphTestKit.Policy(timestamp: t1));
        if (ts2 is { } t2)
            channel = channel.WithPolicy(GraphTestKit.WithDirection(GraphTestKit.Policy(timestamp: t2), 1));

        // Act
        var result = channel.IsStale(1_000 + 1_209_600, TimeSpan.FromSeconds(1_209_600));

        // Assert
        Assert.Equal(stale, result);
    }

    [Fact]
    public void Given_ChannelUpdatePayload_When_BuildingPolicy_Then_FieldsAndFlagsMatch()
    {
        // Arrange
        var update = new ChannelUpdatePayload(ChannelUpdatePayload.EmptySignature, BitcoinNetwork.Regtest.ChainHash,
                                              s_scid, 1234, 0b11, 0b11, 40, 1_000, 5, 6, 9_000);

        // Act
        var policy = GraphPolicy.FromChannelUpdate(update);

        // Assert
        Assert.Equal(1234u, policy.Timestamp);
        Assert.Equal(1, policy.Direction);
        Assert.True(policy.IsDisabled);
        Assert.True(policy.DontForward);
        Assert.Equal((ushort)40, policy.CltvExpiryDelta);
        Assert.Equal(1_000UL, policy.HtlcMinimumMsat);
        Assert.Equal(9_000UL, policy.HtlcMaximumMsat);
        Assert.Equal(5u, policy.FeeBaseMsat);
        Assert.Equal(6u, policy.FeeProportionalMillionths);
        Assert.True(policy.HasSameFieldsAs(update));
    }

    [Fact]
    public void Given_NodeAnnouncementFields_When_BuildingNode_Then_AliasAndColorRead()
    {
        // Arrange
        var alias = new byte[32];
        "NLightning ⚡"u8.CopyTo(alias);
        var address = AddressDescriptor.FromDnsHostname("node.example", 9735);

        // Act
        var node = new GraphNode(s_node1, 7, ReadOnlyMemory<byte>.Empty, alias, [0x12, 0xab, 0xff], [address]);

        // Assert
        Assert.Equal("NLightning ⚡", node.AliasText);
        Assert.Equal("#12abff", node.ColorHex);
        Assert.Equal([address], node.Addresses);
        Assert.False(node.HasUnknownEvenFeatures);
        Assert.Throws<ArgumentException>(() => new GraphNode(s_node1, 7, ReadOnlyMemory<byte>.Empty, new byte[31], new byte[3]));
        Assert.Throws<ArgumentException>(() => new GraphNode(s_node1, 7, ReadOnlyMemory<byte>.Empty, new byte[32], new byte[4]));
    }

    [Fact]
    public void Given_Channels_When_BuildingSnapshot_Then_IndexesAndAdjacencyAreConsistent()
    {
        // Arrange
        var kit = new GraphTestKit();
        var ab = kit.Channel("A", "B", GraphTestKit.Policy(1), GraphTestKit.Policy(2));
        kit.Channel("B", "C", GraphTestKit.Policy(3));
        kit.Announce("B");

        // Act
        var graph = kit.Build();

        // Assert
        Assert.Equal(3, graph.NodeCount);
        Assert.Equal(2, graph.ChannelCount);
        Assert.True(graph.TryGetNodeIndex(kit["B"], out var b));
        Assert.Equal(kit["B"], graph.GetNodeId(b));
        Assert.NotNull(graph.GetNode(b));
        Assert.True(graph.TryGetNode(kit["B"], out _));
        Assert.False(graph.TryGetNode(kit["A"], out _));
        Assert.Single(graph.Nodes);
        Assert.Equal(2, graph.GetAdjacency(b).Count);
        Assert.True(graph.TryGetChannel(ab, out var channel));
        var fromA = graph.GetAdjacency(IndexOf(graph, kit["A"])).Single();
        Assert.Equal(b, fromA.NeighborIndex);
        Assert.Equal(1u, fromA.OutgoingPolicy!.FeeBaseMsat);
        Assert.Equal(2u, fromA.IncomingPolicy!.FeeBaseMsat);
        Assert.Same(channel, fromA.Channel);
    }

    [Fact]
    public void Given_DuplicateChannel_When_BuildingSnapshot_Then_Throws()
    {
        // Arrange
        var channel = new GraphChannel(s_scid, s_node1, s_node2, s_node1, s_node2, 1);

        // Act / Assert
        Assert.Throws<ArgumentException>(() => new GraphSnapshot([channel, channel], []));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("01", false)] // bit 0 (option_data_loss_protect, known)
    [InlineData("02", false)] // bit 1 (odd)
    [InlineData("0100", false)] // bit 8 (var_onion_optin, known)
    [InlineData("04", true)] // bit 2 (unassigned, even)
    [InlineData("400000", false)] // bit 22 (option_anchors, known)
    [InlineData("10" + "000000000000000000000000", true)] // bit 100 (unassigned, even)
    [InlineData("20" + "000000000000000000000000", false)] // bit 101 (odd)
    public void Given_WireFeatures_When_CheckingUnknownEvenBits_Then_OnlyUnknownEvenBitsCount(string hex,
        bool expected)
    {
        // Act / Assert
        Assert.Equal(expected, GossipFeatures.HasUnknownEvenBits(Convert.FromHexString(hex)));
    }

    private static int IndexOf(IGraphView graph, CompactPubKey nodeId)
    {
        Assert.True(graph.TryGetNodeIndex(nodeId, out var index));
        return index;
    }
}