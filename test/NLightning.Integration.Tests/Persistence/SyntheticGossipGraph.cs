using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NLightning.Integration.Tests.Persistence;

using Application.Gossip.Graph.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Addresses;
using Domain.Gossip.Graph;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;

/// <summary>
/// A deterministic synthetic gossip graph for the BOLT 7 G5-T3 persistence measurements: <c>channels</c> announced
/// channels between <c>nodes</c> nodes (a random pair each), both directions' <c>channel_update</c>s and one
/// <c>node_announcement</c> per node with one IPv4 address. Every raw message has its real wire size and parses (the
/// store re-parses updates at load), signatures are random bytes (nothing is verified on this path).
/// </summary>
internal sealed class SyntheticGossipGraph
{
    private static readonly ChainHash s_chainHash = new(SHA256.HashData("nltg-synthetic-chain"u8));

    private SyntheticGossipGraph(List<(GraphChannel Channel, TxId FundingTxId)> channels, List<GraphNode> nodes)
    {
        Channels = channels;
        Nodes = nodes;
    }

    /// <summary>The channels, each with both policies, and its funding txid.</summary>
    public IReadOnlyList<(GraphChannel Channel, TxId FundingTxId)> Channels { get; }

    /// <summary>The node announcements.</summary>
    public IReadOnlyList<GraphNode> Nodes { get; }

    /// <summary>The number of policy directions (two per channel).</summary>
    public int PolicyCount => Channels.Count * 2;

    public static SyntheticGossipGraph Create(int channelCount, int nodeCount, int seed = 7)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(nodeCount, 2);
        var random = new Random(seed);
        var nodeIds = Enumerable.Range(0, nodeCount).Select(i => Key(seed, "node", i)).ToArray();
        const uint baseTimestamp = 1_780_000_000;

        var channels = new List<(GraphChannel, TxId)>(channelCount);
        for (var i = 0; i < channelCount; i++)
        {
            var a = random.Next(nodeCount);
            var b = random.Next(nodeCount - 1);
            if (b >= a)
                b++;

            var (node1, node2) = GraphChannel.CompareNodeIds(nodeIds[a], nodeIds[b]) < 0
                                     ? (nodeIds[a], nodeIds[b])
                                     : (nodeIds[b], nodeIds[a]);
            var scid = new ShortChannelId(500_000 + (uint)(i / 2_000), (uint)(i % 2_000), (ushort)(i % 3));
            var bitcoinKey1 = Key(seed, "btc1", i);
            var bitcoinKey2 = Key(seed, "btc2", i);
            var capacitySat = 100_000UL + (ulong)random.Next(10_000_000);
            var announcement = new ChannelAnnouncementPayload(Signature(random), Signature(random), Signature(random),
                                                              Signature(random), ReadOnlyMemory<byte>.Empty,
                                                              s_chainHash, scid, node1, node2, bitcoinKey1,
                                                              bitcoinKey2);
            var channel = new GraphChannel(scid, node1, node2, bitcoinKey1, bitcoinKey2, capacitySat)
            {
                RawAnnouncement = announcement.GetBytes(),
                Policy1 = Policy(random, scid, 0, baseTimestamp + (uint)i, capacitySat),
                Policy2 = Policy(random, scid, 1, baseTimestamp + (uint)i + 1, capacitySat)
            };
            channels.Add((channel, new TxId(SHA256.HashData(scid))));
        }

        // One IPv4 descriptor (type 1, 4 bytes, port 9735), as most public nodes announce
        byte[] addresses = [0x01, 203, 0, 113, 7, 0x26, 0x07];
        var nodes = new List<GraphNode>(nodeCount);
        for (var i = 0; i < nodeCount; i++)
        {
            var alias = NodeAnnouncementPayload.EncodeAlias($"synthetic-node-{i}");
            byte[] color = [(byte)i, (byte)(i >> 8), (byte)(i >> 16)];
            byte[] features = [0x08, 0xA0, 0x00];
            var announcement = new NodeAnnouncementPayload(Signature(random), features, baseTimestamp + (uint)i,
                                                           nodeIds[i], color, alias, addresses);
            nodes.Add(new GraphNode(nodeIds[i], announcement.Timestamp, features, alias, color,
                                    AddressDescriptorCodec.DecodeList(addresses).Addresses)
            {
                RawAnnouncement = announcement.GetBytes()
            });
        }

        return new SyntheticGossipGraph(channels, nodes);
    }

    /// <summary>Adds everything to <paramref name="store"/> the way the ingress does, one message at a time.</summary>
    public void AddTo(IGraphStore store)
    {
        foreach (var (channel, fundingTxId) in Channels)
        {
            if (!store.TryAddChannel(channel with { Policy1 = null, Policy2 = null }, fundingTxId))
                throw new InvalidOperationException($"Channel {channel.ShortChannelId} was already stored");

            store.TryApplyPolicy(channel.ShortChannelId, channel.Policy1!);
            store.TryApplyPolicy(channel.ShortChannelId, channel.Policy2!);
        }

        foreach (var node in Nodes)
            store.TryApplyNode(node);
    }

    private static GraphPolicy Policy(Random random, ShortChannelId scid, byte direction, uint timestamp,
                                      ulong capacitySat)
    {
        var update = new ChannelUpdatePayload(Signature(random), s_chainHash, scid, timestamp,
                                              ChannelUpdatePayload.MessageFlagMustBeOne, direction,
                                              (ushort)(40 + random.Next(100)), 1_000, (uint)random.Next(2_000),
                                              (uint)random.Next(1_000), capacitySat * 1_000);
        return GraphPolicy.FromChannelUpdate(update) with { RawUpdate = update.GetBytes() };
    }

    private static CompactSignature Signature(Random random)
    {
        var bytes = new byte[ChannelUpdatePayload.SignatureLength];
        random.NextBytes(bytes);
        return new CompactSignature(bytes);
    }

    /// <summary>A distinct 33-byte key (not a curve point: nothing on this path checks it).</summary>
    private static CompactPubKey Key(int seed, string kind, int index)
    {
        Span<byte> input = stackalloc byte[40];
        BinaryPrimitives.WriteInt32BigEndian(input, seed);
        BinaryPrimitives.WriteInt32BigEndian(input[4..], index);
        System.Text.Encoding.ASCII.GetBytes(kind, input[8..]);
        var key = new byte[33];
        key[0] = 0x02;
        SHA256.HashData(input, key.AsSpan(1));
        return new CompactPubKey(key);
    }
}