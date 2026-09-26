using System.Diagnostics.CodeAnalysis;

namespace NLightning.Application.Tests.Gossip.Sync;

using Application.Gossip.Graph;
using Domain.Channels.ValueObjects;
using Domain.Gossip.Graph;
using Domain.Protocol.Constants;
using Domain.Protocol.Payloads;
using Graph;

/// <summary>
/// A graph store filled directly (no ingress) for the query and sync tests: channels between test nodes with their
/// raw announcement, updates and node announcements.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class SyncTestGraph
{
    public static readonly TestGossipKey NodeA = new(0x21);
    public static readonly TestGossipKey NodeB = new(0x22);
    public static readonly TestGossipKey NodeC = new(0x23);
    private static readonly TestGossipKey s_bitcoinA = new(0x31);
    private static readonly TestGossipKey s_bitcoinB = new(0x32);
    private static readonly TestGossipKey s_bitcoinC = new(0x33);

    public SyncTestGraph()
    {
        Kit = new GraphTestKit();
    }

    public GraphTestKit Kit { get; }

    public GraphStore Store => Kit.Store;

    /// <summary>
    /// Adds a channel between two of the test nodes with a signed announcement and, when a timestamp is given, the
    /// signed update of each direction.
    /// </summary>
    public GraphChannel AddSignedChannel(ShortChannelId shortChannelId, TestGossipKey nodeX, TestGossipKey nodeY,
                                         uint? timestamp1 = 1_700_000_000, uint? timestamp2 = 1_700_000_001,
                                         uint? spentAtHeight = null,
                                         GraphChannelVerification verification = GraphChannelVerification.Verified)
    {
        var announcement = GraphTestKit.SignedChannelAnnouncement(shortChannelId, nodeX, nodeY, BitcoinOf(nodeX),
                                                                  BitcoinOf(nodeY)).Payload;
        var channel = new GraphChannel(shortChannelId, announcement.NodeId1, announcement.NodeId2,
                                       announcement.BitcoinKey1, announcement.BitcoinKey2, 1_000_000,
                                       verification: verification)
        {
            RawAnnouncement = announcement.GetBytes(),
            SpentAtHeight = spentAtHeight
        };
        Assert.True(Store.TryAddChannel(channel));
        var node1 = nodeX.PubKey == announcement.NodeId1 ? nodeX : nodeY;
        var node2 = node1 == nodeX ? nodeY : nodeX;
        if (timestamp1 is { } t1)
            ApplyUpdate(GraphTestKit.SignedChannelUpdate(shortChannelId, node1, 0, t1).Payload);
        if (timestamp2 is { } t2)
            ApplyUpdate(GraphTestKit.SignedChannelUpdate(shortChannelId, node2, 1, t2).Payload);

        Assert.True(Store.TryGetChannel(shortChannelId, out var stored));
        return stored;
    }

    /// <summary>
    /// Adds a channel with unsigned (but well-formed) raw bytes and one update per direction: enough for range
    /// replies, fast for large graphs.
    /// </summary>
    public void AddUnsignedChannel(ShortChannelId shortChannelId, uint timestamp = 1_700_000_000)
    {
        var (node1, node2) = Ordered(NodeA, NodeB);
        var announcement = new ChannelAnnouncementPayload(ChannelAnnouncementPayload.EmptySignature,
                                                          ChannelAnnouncementPayload.EmptySignature,
                                                          ChannelAnnouncementPayload.EmptySignature,
                                                          ChannelAnnouncementPayload.EmptySignature,
                                                          ReadOnlyMemory<byte>.Empty, ChainConstants.Regtest,
                                                          shortChannelId, node1.PubKey, node2.PubKey,
                                                          BitcoinOf(node1).PubKey, BitcoinOf(node2).PubKey);
        var channel = new GraphChannel(shortChannelId, node1.PubKey, node2.PubKey, BitcoinOf(node1).PubKey,
                                       BitcoinOf(node2).PubKey, 1_000_000)
        {
            RawAnnouncement = announcement.GetBytes()
        };
        Store.TryAddChannel(channel);
        for (byte direction = 0; direction < 2; direction++)
            ApplyUpdate(new ChannelUpdatePayload(ChannelUpdatePayload.EmptySignature, ChainConstants.Regtest,
                                                 shortChannelId, timestamp + direction,
                                                 ChannelUpdatePayload.MessageFlagMustBeOne, direction, 40, 1_000,
                                                 1_000, 100, 500_000_000));
    }

    /// <summary>Stores a signed node announcement.</summary>
    public GraphNode AddNode(TestGossipKey node, uint timestamp = 1_700_000_000)
    {
        var announcement = GraphTestKit.SignedNodeAnnouncement(node, timestamp).Payload;
        var graphNode = new GraphNode(announcement.NodeId, announcement.Timestamp, announcement.Features,
                                      announcement.Alias.Span, announcement.RgbColor.Span)
        {
            RawAnnouncement = announcement.GetBytes()
        };
        Assert.True(Store.TryApplyNode(graphNode));
        return graphNode;
    }

    public static (TestGossipKey Node1, TestGossipKey Node2) Ordered(TestGossipKey x, TestGossipKey y) =>
        ((byte[])x.PubKey).AsSpan().SequenceCompareTo((byte[])y.PubKey) < 0 ? (x, y) : (y, x);

    private void ApplyUpdate(ChannelUpdatePayload update) =>
        Assert.True(Store.TryApplyPolicy(update.ShortChannelId,
                                         GraphPolicy.FromChannelUpdate(update) with { RawUpdate = update.GetBytes() }));

    private static TestGossipKey BitcoinOf(TestGossipKey node) =>
        node == NodeA ? s_bitcoinA : node == NodeB ? s_bitcoinB : s_bitcoinC;
}