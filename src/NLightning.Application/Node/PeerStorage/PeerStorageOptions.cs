namespace NLightning.Application.Node.PeerStorage;

/// <summary>
/// BOLT 1 peer storage (<c>option_provide_storage</c>), bound from <c>Node:PeerStorage</c>. Whether we offer to keep
/// peers' blobs is the feature bit (<c>Node:Features:OptionProvideStorage</c>); these knobs tune both sides.
/// </summary>
public sealed class PeerStorageOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Node:PeerStorage";

    /// <summary>
    /// Send our own encrypted backup blob (<c>peer_storage</c>) to peers that offer <c>option_provide_storage</c>
    /// (default true).
    /// </summary>
    public bool SendBackups { get; set; } = true;

    /// <summary>
    /// Keep the blob of a peer we have no channel with (BOLT 1 MAY; default false: only peers with a channel, which
    /// bounds what we store).
    /// </summary>
    public bool StoreWithoutChannel { get; set; }

    /// <summary>
    /// At most one write of a peer's blob to the database per this interval (BOLT 1: MAY delay storage to one update
    /// per minute; default 1 minute). The latest blob is always the one handed back, even before it is written.
    /// </summary>
    public TimeSpan MinStoreInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How often our backup blob is rebuilt and, when what it holds changed, sent again to every connected peer that
    /// stores it (default 1 minute). Each new connection gets it at once.
    /// </summary>
    public TimeSpan BackupInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// At the first connection of the process to a peer that stores our backup, how long our backup waits for the
    /// peer's <c>peer_storage_retrieval</c> before it is sent (default 30 seconds): the copy the peer keeps may name
    /// channels we lost and must be read before it is replaced. Zero sends at once.
    /// </summary>
    public TimeSpan RetrievalWait { get; set; } = TimeSpan.FromSeconds(30);
}