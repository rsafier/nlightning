namespace NLightning.Application.Gossip.Graph;

using Domain.Gossip.Graph;

/// <summary>
/// The memory model behind <see cref="GraphMemoryEstimate"/> (BOLT 7 plan G5-T1/G5-T3): the variable-size bytes of each
/// message plus a fixed cost per channel, policy and node.
/// </summary>
/// <remarks>
/// <para>
/// The fixed costs were measured on .NET 10.0.3 (Release, arm64, workstation GC) by loading synthetic graphs from
/// SQLite into a new store and reading the retained managed heap (<c>GC.GetTotalMemory</c> after full compacting
/// collections, <c>GraphStoreLoadPerformanceTests</c>), one kind at a time: 200,000 channels alone retained 844 bytes
/// per channel beyond their variable bytes, their 400,000 policies 215 bytes per policy, 50,000 node announcements
/// alone 1,000 bytes per node; a snapshot 180 bytes per channel and 100 per node. The constants round the per-channel
/// and per-policy costs up so the whole 200,000-channel graph (475.0 MiB measured, 468.5 MiB estimated; the snapshot
/// 39.1 MiB both) is estimated within 2 %. Node ids are not interned yet (G5-T1), so every channel carries its own copies of its two node ids.
/// </para>
/// </remarks>
public static class GraphMemoryAccounting
{
    /// <summary>
    /// Per channel: the <see cref="GraphChannel"/>, its short channel id and four keys, and its entries in the store's
    /// channel, arrival time, funding txid and funding outpoint dictionaries.
    /// </summary>
    public const long ChannelFixedBytes = 950;

    /// <summary>Per policy direction: the <see cref="GraphPolicy"/> and its raw update's array.</summary>
    public const long PolicyFixedBytes = 250;

    /// <summary>Per node announcement: the <see cref="GraphNode"/>, its key, alias, color and dictionary entries.</summary>
    public const long NodeFixedBytes = 1_000;

    /// <summary>Per address descriptor of a node announcement.</summary>
    public const long AddressBytes = 64;

    /// <summary>Per channel in a <see cref="GraphSnapshot"/>: its dictionary entry and two adjacency entries.</summary>
    public const long SnapshotChannelBytes = 180;

    /// <summary>Per node in a <see cref="GraphSnapshot"/>: its index, id, node and adjacency list.</summary>
    public const long SnapshotNodeBytes = 100;

    /// <summary>The channel's raw announcement, features and both policies' raw updates and trailing fields.</summary>
    public static long VariableBytesOf(GraphChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return channel.RawAnnouncement.Length + channel.Features.Length + VariableBytesOf(channel.Policy1)
             + VariableBytesOf(channel.Policy2);
    }

    /// <summary>The node's raw announcement, features and addresses.</summary>
    public static long VariableBytesOf(GraphNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.RawAnnouncement.Length + node.Features.Length + node.Addresses.Count * AddressBytes;
    }

    /// <summary>The number of stored directions of <paramref name="channel"/> (0, 1 or 2).</summary>
    public static int PolicyCountOf(GraphChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return (channel.Policy1 is null ? 0 : 1) + (channel.Policy2 is null ? 0 : 1);
    }

    /// <summary>The estimate for a graph of these counts.</summary>
    /// <param name="channels">The channels.</param>
    /// <param name="policies">The policy directions.</param>
    /// <param name="nodes">The node announcements.</param>
    /// <param name="snapshotNodes">The nodes a snapshot indexes (channel ends and announced nodes).</param>
    /// <param name="variableBytes">The summed <c>VariableBytesOf</c> of every channel and node.</param>
    public static GraphMemoryEstimate Estimate(int channels, int policies, int nodes, int snapshotNodes,
                                               long variableBytes)
    {
        var store = variableBytes + channels * ChannelFixedBytes + policies * PolicyFixedBytes
                  + nodes * NodeFixedBytes;
        var snapshot = channels * SnapshotChannelBytes + snapshotNodes * SnapshotNodeBytes;
        return new GraphMemoryEstimate(channels, policies, nodes, variableBytes, store, snapshot);
    }

    private static long VariableBytesOf(GraphPolicy? policy) =>
        policy is null ? 0 : policy.RawUpdate.Length + policy.ExtraData.Length;
}