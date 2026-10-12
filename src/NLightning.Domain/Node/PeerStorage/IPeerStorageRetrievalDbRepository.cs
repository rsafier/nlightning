namespace NLightning.Domain.Node.PeerStorage;

using Crypto.ValueObjects;

/// <summary>
/// The <c>peer_storage_retrieval</c>s our peers sent us (table <c>PeerStorageRetrievals</c>, migration
/// <c>AddPeerStorageRetrievals</c>, NL-432): one row per peer, the latest retrieval only, kept across restarts so the
/// operator can read the peer's copy of our backup after a data loss.
/// </summary>
public interface IPeerStorageRetrievalDbRepository
{
    /// <summary>
    /// The latest retrieval of <paramref name="peerNodeId"/>, or null.
    /// </summary>
    Task<StoredPeerRetrieval?> GetAsync(CompactPubKey peerNodeId);

    /// <summary>
    /// The latest retrieval of every peer.
    /// </summary>
    Task<IReadOnlyList<StoredPeerRetrieval>> GetAllAsync();

    /// <summary>
    /// Stages the peer's retrieval, replacing the one kept before (saved with the unit of work).
    /// </summary>
    Task UpsertAsync(StoredPeerRetrieval retrieval);
}