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
/// that passes <see cref="SeedAddressFilter"/>, or a valid Tor v3 onion service when the caller dials through Tor;
/// DNS hostnames are skipped: a hostname from gossip would make us resolve names a remote chose), sets no unknown even feature, is not excluded
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
    /// <param name="failedEndpoints">Endpoints whose dial failed recently (<see cref="SeedPeerCandidate.Endpoint"/>).
    /// </param>
    /// <param name="families">The address families to dial.</param>
    /// <param name="allowNonRoutable">Accept private and other non-routable addresses (local tests).</param>
    /// <param name="limit">The most candidates returned.</param>
    /// <param name="random">The shuffle's source.</param>
    /// <param name="onions">Whether Tor v3 onion services can be dialed, and whether they are preferred over IP
    /// addresses (Tor-only) or used only for nodes without a usable IP address.</param>
    public static List<SeedPeerCandidate> Select(IGraphView graph, ulong nowUnixSeconds,
                                                 IReadOnlySet<CompactPubKey> excluded,
                                                 IReadOnlySet<(string, ushort)> failedEndpoints,
                                                 DnsSeedAddressTypes families, bool allowNonRoutable, int limit,
                                                 Random random, OnionCandidates onions = OnionCandidates.None)
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

            var candidate = TryGetCandidate(node, failedEndpoints, families, allowNonRoutable, onions);
            if (candidate is null)
                continue;

            var active = CountActiveChannels(graph, node.NodeId, nowUnixSeconds, maxAgeSeconds);
            if (active == 0)
                continue;

            var recentAnnouncement = (ulong)node.Timestamp + maxAgeSeconds >= nowUnixSeconds;
            (recentAnnouncement && active >= GoodMinActiveChannels ? good : other).Add(candidate.Value);
        }

        var goodArray = good.ToArray();
        var otherArray = other.ToArray();
        random.Shuffle(goodArray);
        random.Shuffle(otherArray);
        return [.. goodArray.Concat(otherArray).DistinctBy(c => c.Endpoint).Take(limit)];
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

    private static SeedPeerCandidate? TryGetCandidate(GraphNode node, IReadOnlySet<(string, ushort)> failedEndpoints,
                                                      DnsSeedAddressTypes families, bool allowNonRoutable,
                                                      OnionCandidates onions)
    {
        SeedPeerCandidate? ip = null;
        SeedPeerCandidate? onion = null;
        foreach (var descriptor in node.Addresses)
        {
            if (descriptor.Type == AddressDescriptorType.TorV3)
            {
                if (onion is not null || onions == OnionCandidates.None || descriptor.Port == 0
                 || !OnionV3Address.IsValid(descriptor.Address))
                    continue;

                var host = OnionV3Address.ToHostName(descriptor.Address);
                if (failedEndpoints.Contains((host, descriptor.Port)))
                    continue;

                onion = new SeedPeerCandidate(node.NodeId, IPAddress.None, descriptor.Port, GraphSource)
                {
                    OnionHost = host
                };
                continue;
            }

            if (ip is not null)
                continue;

            var family = descriptor.Type switch
            {
                AddressDescriptorType.IPv4 => DnsSeedAddressTypes.IPv4,
                AddressDescriptorType.IPv6 => DnsSeedAddressTypes.IPv6,
                _ => (DnsSeedAddressTypes)0
            };
            if ((families & family) == 0)
                continue;

            var address = new IPAddress(descriptor.Address);
            if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                continue;
            if (!SeedAddressFilter.IsUsable(address, descriptor.Port, allowNonRoutable, out _))
                continue;
            if (failedEndpoints.Contains((address.ToString(), descriptor.Port)))
                continue;

            ip = new SeedPeerCandidate(node.NodeId, address, descriptor.Port, GraphSource);
        }

        return onions == OnionCandidates.Preferred ? onion ?? ip : ip ?? onion;
    }
}

/// <summary>
/// Whether <see cref="GraphPeerCandidateSelector"/> picks Tor v3 onion services.
/// </summary>
public enum OnionCandidates
{
    /// <summary>Never (no Tor).</summary>
    None,

    /// <summary>For nodes without a usable IP address (Tor for onions only).</summary>
    Fallback,

    /// <summary>Before a node's IP address (Tor-only).</summary>
    Preferred
}