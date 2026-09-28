using System.Net;
using System.Net.Sockets;

namespace NLightning.Domain.Node.Bootstrap;

using Crypto.ValueObjects;
using Gossip.Addresses;
using Gossip.Graph;

/// <summary>
/// Picks peers to dial from the network graph when the node has too few peers (NL-543): the graph top-up that runs
/// before the BOLT 10 DNS seeds are asked.
/// </summary>
/// <remarks>
/// <para>A node is a candidate only when it announced itself with a usable address (IPv4 or IPv6 of an asked family
/// that passes <see cref="SeedAddressFilter"/>; Tor and DNS hostnames are skipped: the node has no Tor proxy, and a
/// hostname from gossip would make us resolve names a remote chose), sets no unknown even feature, is not excluded
/// (ourselves, connected peers) and has at least one active channel: unspent, with an enabled <c>channel_update</c>
/// of its own direction newer than <see cref="MaxAge"/>.</para>
/// <para>Good candidates come first: a <c>node_announcement</c> newer than <see cref="MaxAge"/> and at least
/// <see cref="GoodMinActiveChannels"/> active channels. The order is random within each tier, so restarts and many
/// nodes do not all dial the same hubs. One address per node, one node per (address, port).</para>
/// </remarks>
public static class GraphPeerCandidateSelector
{
    /// <summary>The <see cref="SeedPeerCandidate.Seed"/> of a candidate taken from the graph.</summary>
    public const string GraphSource = "graph";

    /// <summary>The age past which a <c>channel_update</c> or <c>node_announcement</c> no longer counts as recent.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    /// <summary>The active channels a good candidate has at least.</summary>
    public const int GoodMinActiveChannels = 2;

    /// <summary>
    /// The candidates, good ones first, at most <paramref name="limit"/>.
    /// </summary>
    /// <param name="graph">The graph snapshot.</param>
    /// <param name="nowUnixSeconds">The current time.</param>
    /// <param name="excluded">Node ids never to pick (ourselves, connected peers).</param>
    /// <param name="failedEndpoints">Endpoints whose dial failed recently.</param>
    /// <param name="families">The address families to dial.</param>
    /// <param name="allowNonRoutable">Accept private and other non-routable addresses (local tests).</param>
    /// <param name="limit">The most candidates returned.</param>
    /// <param name="random">The shuffle's source.</param>
    public static List<SeedPeerCandidate> Select(IGraphView graph, ulong nowUnixSeconds,
                                                 IReadOnlySet<CompactPubKey> excluded,
                                                 IReadOnlySet<(IPAddress, ushort)> failedEndpoints,
                                                 DnsSeedAddressTypes families, bool allowNonRoutable, int limit,
                                                 Random random)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(excluded);
        ArgumentNullException.ThrowIfNull(failedEndpoints);
        ArgumentNullException.ThrowIfNull(random);
        if (limit <= 0)
            return [];

        var maxAgeSeconds = (ulong)MaxAge.TotalSeconds;
        var good = new List<SeedPeerCandidate>();
        var other = new List<SeedPeerCandidate>();
        foreach (var node in graph.Nodes)
        {
            if (node.HasUnknownEvenFeatures || excluded.Contains(node.NodeId))
                continue;

            if (!TryGetAddress(node, failedEndpoints, families, allowNonRoutable, out var address, out var port))
                continue;

            var active = CountActiveChannels(graph, node.NodeId, nowUnixSeconds, maxAgeSeconds);
            if (active == 0)
                continue;

            var candidate = new SeedPeerCandidate(node.NodeId, address, port, GraphSource);
            var recentAnnouncement = (ulong)node.Timestamp + maxAgeSeconds >= nowUnixSeconds;
            (recentAnnouncement && active >= GoodMinActiveChannels ? good : other).Add(candidate);
        }

        var goodArray = good.ToArray();
        var otherArray = other.ToArray();
        random.Shuffle(goodArray);
        random.Shuffle(otherArray);
        return [.. goodArray.Concat(otherArray).DistinctBy(c => (c.Address, c.Port)).Take(limit)];
    }

    private static int CountActiveChannels(IGraphView graph, CompactPubKey nodeId, ulong now, ulong maxAgeSeconds)
    {
        if (!graph.TryGetNodeIndex(nodeId, out var index))
            return 0;

        var active = 0;
        foreach (var adjacency in graph.GetAdjacency(index))
        {
            if (adjacency.Channel.SpentAtHeight is not null)
                continue;

            if (adjacency.OutgoingPolicy is { IsDisabled: false } policy
             && (ulong)policy.Timestamp + maxAgeSeconds >= now)
                active++;
        }

        return active;
    }

    private static bool TryGetAddress(GraphNode node, IReadOnlySet<(IPAddress, ushort)> failedEndpoints,
                                      DnsSeedAddressTypes families, bool allowNonRoutable, out IPAddress address,
                                      out ushort port)
    {
        foreach (var descriptor in node.Addresses)
        {
            var family = descriptor.Type switch
            {
                AddressDescriptorType.IPv4 => DnsSeedAddressTypes.IPv4,
                AddressDescriptorType.IPv6 => DnsSeedAddressTypes.IPv6,
                _ => (DnsSeedAddressTypes)0
            };
            if ((families & family) == 0)
                continue;

            var ip = new IPAddress(descriptor.Address);
            if (ip.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                continue;
            if (!SeedAddressFilter.IsUsable(ip, descriptor.Port, allowNonRoutable, out _))
                continue;
            if (failedEndpoints.Contains((ip, descriptor.Port)))
                continue;

            address = ip;
            port = descriptor.Port;
            return true;
        }

        address = IPAddress.None;
        port = 0;
        return false;
    }
}