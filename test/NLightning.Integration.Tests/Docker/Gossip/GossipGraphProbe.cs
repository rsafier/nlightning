using Grpc.Core;
using Lnrpc;
using LNUnit.LND;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Docker.Gossip;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Persistence;
using Domain.Persistence.Interfaces;
using Utils;

/// <summary>
/// What the BOLT 7 proofs read: an LND node's graph (<c>GetChanInfo</c>, <c>DescribeGraph</c>, <c>GetNodeInfo</c>) and
/// our node's graph. Every read is logged, and every assertion is about the test's own channels and nodes (the shared
/// alice relays other tests' channels).
/// </summary>
public static class GossipGraphProbe
{
    /// <summary>
    /// How often the proofs poll a graph: every read is logged, so not at <see cref="Poll"/>'s 100 ms default.
    /// </summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// LND's edge for <paramref name="shortChannelId"/>, or null while LND does not know the channel.
    /// </summary>
    public static async Task<ChannelEdge?> TryGetChanInfoAsync(LNDNodeConnection lnd, ulong shortChannelId,
                                                               CancellationToken cancellationToken)
    {
        try
        {
            var edge = await lnd.LightningClient.GetChanInfoAsync(new ChanInfoRequest { ChanId = shortChannelId },
                                                                  cancellationToken: cancellationToken);
            Console.WriteLine($"{lnd.LocalAlias} GetChanInfo({shortChannelId}): node1 {edge.Node1Pub} "
                            + $"policy={Describe(edge.Node1Policy)}, node2 {edge.Node2Pub} "
                            + $"policy={Describe(edge.Node2Policy)}, capacity {edge.Capacity}");
            return edge;
        }
        catch (RpcException e) when (e.StatusCode is StatusCode.NotFound or StatusCode.Unknown)
        {
            // LND answers "edge not found" (NotFound in 0.20, Unknown in older versions) for a channel it never saw
            return null;
        }
    }

    /// <summary>
    /// Whether LND's <c>DescribeGraph</c> (announced channels only) lists <paramref name="shortChannelId"/>.
    /// </summary>
    public static async Task<bool> GraphHasChannelAsync(LNDNodeConnection lnd, ulong shortChannelId,
                                                        CancellationToken cancellationToken)
    {
        var graph = await lnd.LightningClient.DescribeGraphAsync(new ChannelGraphRequest { IncludeUnannounced = false },
                                                                 cancellationToken: cancellationToken);
        var found = graph.Edges.Any(e => e.ChannelId == shortChannelId);
        Console.WriteLine($"{lnd.LocalAlias} DescribeGraph: {graph.Nodes.Count} nodes, {graph.Edges.Count} edges, "
                        + $"{shortChannelId} {(found ? "present" : "absent")}");
        return found;
    }

    /// <summary>
    /// LND's announcement of <paramref name="nodeIdHex"/>, or null while it has none.
    /// </summary>
    public static async Task<LightningNode?> TryGetNodeInfoAsync(LNDNodeConnection lnd, string nodeIdHex,
                                                                 CancellationToken cancellationToken)
    {
        try
        {
            var info = await lnd.LightningClient.GetNodeInfoAsync(new NodeInfoRequest { PubKey = nodeIdHex },
                                                                  cancellationToken: cancellationToken);
            Console.WriteLine($"{lnd.LocalAlias} GetNodeInfo({nodeIdHex[..16]}…): alias '{info.Node?.Alias}', "
                            + $"color {info.Node?.Color}, last update {info.Node?.LastUpdate}, "
                            + $"{info.NumChannels} channels");
            // LND keeps a node with a zero LastUpdate for a channel endpoint it has no node_announcement of
            return info.Node is { LastUpdate: > 0 } ? info.Node : null;
        }
        catch (RpcException e) when (e.StatusCode is StatusCode.NotFound or StatusCode.Unknown)
        {
            return null;
        }
    }

    /// <summary>
    /// The LND-LND channels of the fixture (alice-bob twice, alice-carol, bob-carol) as LND lists them: short channel
    /// id, the two node ids, and whether LND marks the channel private.
    /// </summary>
    public static async Task<IReadOnlyList<(ulong ShortChannelId, string Local, string Remote, bool Private)>>
        GetFixtureChannelsAsync(IReadOnlyList<LNDNodeConnection> lndNodes, CancellationToken cancellationToken)
    {
        var ids = lndNodes.Select(n => n.LocalNodePubKey.ToLowerInvariant()).ToHashSet();
        var channels = new Dictionary<ulong, (ulong, string, string, bool)>();
        foreach (var lnd in lndNodes)
        {
            var list = await lnd.LightningClient.ListChannelsAsync(new ListChannelsRequest(),
                                                                   cancellationToken: cancellationToken);
            foreach (var channel in list.Channels.Where(c => ids.Contains(c.RemotePubkey.ToLowerInvariant())))
                channels.TryAdd(channel.ChanId, (channel.ChanId, lnd.LocalNodePubKey.ToLowerInvariant(),
                                                 channel.RemotePubkey.ToLowerInvariant(), channel.Private));
        }

        foreach (var (scid, local, remote, isPrivate) in channels.Values)
            Console.WriteLine($"Fixture channel {scid} ({new ShortChannelId(scid)}) {local[..16]}… - {remote[..16]}… "
                            + $"private={isPrivate}");
        return channels.Values.ToList();
    }

    /// <summary>
    /// Our node's persisted graph: the channel, its stored policies, or null when it is not stored.
    /// </summary>
    /// <remarks>
    /// TODO(G-B integrator): Proof G2 reads <c>listgraphchannels</c> (IPC 18) and <c>listnodes</c> (IPC 17). Their
    /// request/response types did not exist when lane B3 wrote this, so the probe reads the store behind them
    /// (<see cref="IUnitOfWork.GraphDbRepository"/>, migration <c>AddGossipGraph</c>): switch these two reads to the
    /// client handlers (as <c>Abcd/NodeClientCalls</c> does) once B2 (G2-T6) is merged. The store is written behind
    /// (plan D2), so callers poll.
    /// </remarks>
    public static async Task<(GraphChannelRecord Channel, IReadOnlyList<GraphPolicyRecord> Policies)?>
        TryGetOurGraphChannelAsync(NLightningTestNode node, ulong shortChannelId)
    {
        using var scope = node.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().GraphDbRepository;
        var scid = new ShortChannelId(shortChannelId);
        var channel = await repository.GetChannelAsync(scid);
        if (channel is null)
            return null;

        var policies = await repository.GetPoliciesAsync(scid);
        return (channel, policies);
    }

    /// <summary>
    /// Our node's persisted announcement of <paramref name="nodeId"/>, or null (see
    /// <see cref="TryGetOurGraphChannelAsync"/> for the TODO).
    /// </summary>
    public static async Task<GraphNodeRecord?> TryGetOurGraphNodeAsync(NLightningTestNode node, byte[] nodeId)
    {
        using var scope = node.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().GraphDbRepository
                          .GetNodeAsync(new CompactPubKey(nodeId));
    }

    /// <summary>
    /// Whether our graph has every one of <paramref name="shortChannelIds"/> with both directions' policies and an
    /// announcement of every node in <paramref name="nodeIds"/>. Logs what is missing.
    /// </summary>
    public static async Task<bool> OurGraphHasAsync(NLightningTestNode node, IEnumerable<ulong> shortChannelIds,
                                                    IEnumerable<LNDNodeConnection> nodeIds)
    {
        var missing = new List<string>();
        foreach (var scid in shortChannelIds)
        {
            var stored = await TryGetOurGraphChannelAsync(node, scid);
            if (stored is null)
                missing.Add($"channel {new ShortChannelId(scid)}");
            else if (stored.Value.Policies.Select(p => p.Direction).Distinct().Count() < 2)
                missing.Add($"policies of {new ShortChannelId(scid)} ({stored.Value.Policies.Count} stored)");
        }

        foreach (var lnd in nodeIds)
            if (await TryGetOurGraphNodeAsync(node, lnd.LocalNodePubKeyBytes) is null)
                missing.Add($"node {lnd.LocalAlias}");

        Console.WriteLine(missing.Count == 0
                              ? $"[{node.Name}] graph complete"
                              : $"[{node.Name}] graph missing: {string.Join(", ", missing)}");
        return missing.Count == 0;
    }

    /// <summary>
    /// Polls <paramref name="condition"/>; after each <paramref name="blockEvery"/> without success mines one block
    /// (announcements wait for 6 confirmations on both ends, and LND re-checks on each block), up to
    /// <paramref name="maxBlocks"/> blocks.
    /// </summary>
    public static async Task MineUntilAsync(Func<Task<bool>> condition, Func<Task> mineOne, TimeSpan blockEvery,
                                            int maxBlocks, TimeSpan finalWait, string description,
                                            CancellationToken cancellationToken)
    {
        for (var mined = 0; mined < maxBlocks; mined++)
        {
            try
            {
                await Poll.UntilAsync(condition, blockEvery, description, cancellationToken, PollInterval);
                return;
            }
            catch (TimeoutException)
            {
                await mineOne();
            }
        }

        await Poll.UntilAsync(condition, finalWait, description, cancellationToken, PollInterval);
    }

    private static string Describe(RoutingPolicy? policy) =>
        policy is null
            ? "none"
            : $"(base {policy.FeeBaseMsat}, ppm {policy.FeeRateMilliMsat}, delta {policy.TimeLockDelta}, "
            + $"min {policy.MinHtlc}, max {policy.MaxHtlcMsat}, disabled {policy.Disabled}, "
            + $"updated {policy.LastUpdate})";
}