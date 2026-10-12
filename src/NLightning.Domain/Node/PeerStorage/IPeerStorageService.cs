namespace NLightning.Domain.Node.PeerStorage;

using Crypto.ValueObjects;
using Interfaces;
using Protocol.Interfaces;

/// <summary>
/// BOLT 1 peer storage (<c>option_provide_storage</c>) on both sides: as a provider it keeps the latest
/// <c>peer_storage</c> blob of each peer we have a channel with and hands it back with <c>peer_storage_retrieval</c>
/// after every <c>init</c> exchange; as a client it sends our own encrypted backup blob
/// (<see cref="IPeerBackupBlobProvider"/>) to peers that offer the feature and reads what they hand back.
/// </summary>
/// <remarks>
/// Called by the peer service on the transport read loop, so both hooks only queue work (except the retrieval of a
/// stored blob, which is enqueued on the connection before the hook returns, so it precedes our
/// <c>channel_reestablish</c>). Never throws.
/// </remarks>
public interface IPeerStorageService
{
    /// <summary>
    /// Called once per connection, after both <c>init</c> messages went out (BOLT 1: nothing precedes init). Sends
    /// the peer the blob we keep for it (<c>peer_storage_retrieval</c>), then, when the peer offers
    /// <c>option_provide_storage</c>, our own backup blob.
    /// </summary>
    void OnPeerInitialized(IPeerService peer);

    /// <summary>
    /// Handles a <c>peer_storage</c> or <c>peer_storage_retrieval</c> from <paramref name="peer"/>.
    /// </summary>
    void HandleMessage(IPeerService peer, IMessage message);

    /// <summary>
    /// Handles a <c>warning</c> (never an <c>error</c>) from <paramref name="peer"/>. A peer that refused our backup
    /// blob for its size is learned from it: the next backup to it is built within the byte limit its warning names
    /// and the refusal is listed by <see cref="GetRefusals"/>. Only an unambiguous size refusal counts (NL-563): a
    /// peer-storage warning that names no byte limit (e.g. LDK before any channel with it is funded) is informational
    /// and changes nothing. Only queues work; never throws.
    /// </summary>
    void HandleWarning(IPeerService peer, string message);

    /// <summary>
    /// The peers that refused our backup blob for its size, with the limit each accepts (since the start; in memory:
    /// after a restart the first blob is refused again and the limit relearned, NL-559).
    /// </summary>
    IReadOnlyList<PeerStorageRefusalReport> GetRefusals();

    /// <summary>
    /// What our peers handed back to us since the start (the latest per peer), for the restore flow.
    /// </summary>
    IReadOnlyList<PeerBackupRetrieval> GetRetrievals();

    /// <summary>
    /// The blob we keep for <paramref name="peerNodeId"/>, or null.
    /// </summary>
    Task<StoredPeerBlob?> GetStoredBlobAsync(CompactPubKey peerNodeId);

    /// <summary>
    /// The latest <c>peer_storage_retrieval</c> of every peer, kept across restarts (table
    /// <c>PeerStorageRetrievals</c>, NL-432), read again with the blob provider, each channel checked against the
    /// database now; ordered by arrival. A retrieval whose write failed is included from memory.
    /// </summary>
    Task<IReadOnlyList<PeerStorageRetrievalReport>> ListRetrievalsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The blobs we keep for our peers (as a provider), the latest of each, including writes still delayed; ordered
    /// by peer.
    /// </summary>
    Task<IReadOnlyList<StoredPeerBlob>> ListStoredBlobsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// True once a peer handed back a backup of ours naming channels we have no record of: from then on (until the
    /// restart) our backup is sent to no peer, so the copies that prove the data loss are not overwritten.
    /// </summary>
    bool BackupsHeldForDataLoss { get; }

    /// <summary>
    /// Stops the periodic round and writes the blobs whose write was delayed. Call it at shutdown after the peers
    /// stopped and before the service provider is disposed (disposal alone cannot open a database scope any more).
    /// </summary>
    Task StopAsync();
}