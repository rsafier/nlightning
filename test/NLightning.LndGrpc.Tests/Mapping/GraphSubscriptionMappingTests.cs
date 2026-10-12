using System.Text;

namespace NLightning.LndGrpc.Tests.Mapping;

using Application.Gossip.Graph;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Graph;
using LndGrpc.Services;

public sealed class GraphSubscriptionMappingTests
{
    private static readonly CompactPubKey s_first = new(Convert.FromHexString("02" + new string('1', 64)));
    private static readonly CompactPubKey s_second = new(Convert.FromHexString("03" + new string('2', 64)));
    private static readonly ShortChannelId s_scid = new(250, 3, 1);
    private static readonly TxId s_txId = new(Enumerable.Repeat((byte)7, 32).ToArray());

    [Fact]
    public void Given_ADirectedPolicy_When_Mapped_Then_ChannelIdsAmountsAndDirectionMatchLnd()
    {
        var channel = Channel();
        var policy = new GraphPolicy(123, 1, 3, 144, 1000, 900_000, 1200, 450)
        {
            InboundFeeBaseMsat = unchecked((uint)-5),
            InboundFeeProportionalMillionths = unchecked((uint)-7)
        };
        var update = LightningService.ToGraphTopologyUpdate(new GraphChange(channel.WithPolicy(policy),
            Policy: policy, FundingTxId: s_txId));

        var edge = Assert.Single(update.ChannelUpdates);
        Assert.Equal(LightningService.ToChanId(s_scid), edge.ChanId);
        Assert.Equal(s_txId.ToString(), edge.ChanPoint.FundingTxidStr);
        Assert.Equal(1U, edge.ChanPoint.OutputIndex);
        Assert.Equal(1_000_000, edge.Capacity);
        Assert.Equal(s_second.ToString(), edge.AdvertisingNode);
        Assert.Equal(s_first.ToString(), edge.ConnectingNode);
        Assert.True(edge.RoutingPolicy.Disabled);
        Assert.Equal(1000, edge.RoutingPolicy.MinHtlc);
        Assert.Equal(900_000UL, edge.RoutingPolicy.MaxHtlcMsat);
        Assert.Equal(1200, edge.RoutingPolicy.FeeBaseMsat);
        Assert.Equal(450, edge.RoutingPolicy.FeeRateMilliMsat);
        Assert.Equal(144U, edge.RoutingPolicy.TimeLockDelta);
        Assert.Equal(-5, edge.RoutingPolicy.InboundFeeBaseMsat);
        Assert.Equal(-7, edge.RoutingPolicy.InboundFeeRateMilliMsat);
    }

    [Fact]
    public void Given_ATaprootGraphEdge_When_Restored_Then_ItUsesTheSamePreferredPolicyAsDescribeGraph()
    {
        var v1 = new GraphPolicy(1_700_000_000, 1, 0, 40, 1, 10_000, 1, 2);
        var v2 = v1 with { GossipVersion = 2, Timestamp = 250, FeeBaseMsat = 100 };
        var channel = Channel().WithPolicy(v1).WithPolicy(v2) with { Versions = GraphGossipVersions.V2 };

        var update = LightningService.ToGraphTopologyUpdate(new GraphChange(channel, FundingTxId: s_txId));

        Assert.Equal(2, update.ChannelUpdates.Count);
        Assert.Equal(100, update.ChannelUpdates[0].RoutingPolicy.FeeBaseMsat);
        Assert.Equal(250U, update.ChannelUpdates[0].RoutingPolicy.LastUpdate);
        Assert.Null(update.ChannelUpdates[1].RoutingPolicy);
    }

    [Fact]
    public void Given_ARemovedEdge_When_Mapped_Then_ItsSnapshotRetainsTheFundingPointAndActualCloseHeight()
    {
        var update = LightningService.ToGraphTopologyUpdate(new GraphChange(Channel(), FundingTxId: s_txId,
            Removed: true, ClosedHeight: 300));

        var closed = Assert.Single(update.ClosedChans);
        Assert.Empty(update.ChannelUpdates);
        Assert.Equal(LightningService.ToChanId(s_scid), closed.ChanId);
        Assert.Equal(s_txId.ToString(), closed.ChanPoint.FundingTxidStr);
        Assert.Equal(1_000_000, closed.Capacity);
        Assert.Equal(300U, closed.ClosedHeight);
    }

    [Fact]
    public void Given_AMergedNode_When_Mapped_Then_TheLatestAliasColorAndFeaturesArePublished()
    {
        var alias = new byte[32];
        Encoding.UTF8.GetBytes("graph node").CopyTo(alias, 0);
        var node = new GraphNode(s_first, 123, new byte[] { 1 }, alias, new byte[] { 0x12, 0x34, 0x56 });

        var update = LightningService.ToGraphTopologyUpdate(new GraphChange(Node: node));

        var item = Assert.Single(update.NodeUpdates);
        Assert.Equal(s_first.ToString(), item.IdentityKey);
        Assert.Equal("graph node", item.Alias);
        Assert.Equal("#123456", item.Color);
        Assert.True(item.Features.ContainsKey(0));
        Assert.Empty(update.ChannelUpdates);
    }

    private static GraphChannel Channel() => new(s_scid, s_first, s_second, s_first, s_second, 1_000_000);
}