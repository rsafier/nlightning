namespace NLightning.Application.Gossip.Graph;

/// <summary>
/// What the in-memory graph holds and about how much managed memory it takes (BOLT 7 plan G5-T1/G5-T3):
/// <see cref="GraphStore.GetMemoryEstimate"/> keeps the counts and the variable-size bytes up to date on every change,
/// so reading it is O(1) and can guard every add.
/// </summary>
/// <param name="Channels">The channels (spent ones included).</param>
/// <param name="Policies">The <c>channel_update</c> directions stored with them.</param>
/// <param name="Nodes">The node announcements.</param>
/// <param name="VariableBytes">
/// The bytes whose size depends on the message: raw signed announcements and updates, feature bits, unknown trailing
/// fields and address descriptors.
/// </param>
/// <param name="StoreBytes">
/// The estimated managed memory of the store: <see cref="VariableBytes"/> plus the measured fixed cost per channel,
/// policy and node (objects, keys, dictionary entries; calibrated on the 200,000-channel SQLite load,
/// <c>GraphStoreLoadPerformanceTests</c>).
/// </param>
/// <param name="SnapshotBytes">
/// The estimated extra memory of one <see cref="Domain.Gossip.Graph.GraphSnapshot"/> of this graph (its dictionaries
/// and adjacency lists; the channel and node objects are shared with the store). Pathfinding and the relay hold one
/// while the next is built, so budget up to twice this.
/// </param>
public sealed record GraphMemoryEstimate(
    int Channels,
    int Policies,
    int Nodes,
    long VariableBytes,
    long StoreBytes,
    long SnapshotBytes)
{
    /// <summary>The store plus one snapshot, in bytes.</summary>
    public long TotalBytes => StoreBytes + SnapshotBytes;

    /// <summary><see cref="TotalBytes"/> in MiB, rounded up (the unit of <c>Gossip:MaxMemoryMb</c>).</summary>
    public long TotalMegabytes => (TotalBytes + (1 << 20) - 1) >> 20;
}