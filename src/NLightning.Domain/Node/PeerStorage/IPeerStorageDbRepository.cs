namespace NLightning.Domain.Node.PeerStorage;

using Crypto.ValueObjects;

/// <summary>
/// The blobs peers asked us to keep (table <c>PeerStorageBlobs</c>, migration <c>AddPeerStorage</c>): one row per peer,
/// the latest <c>peer_storage</c> only.
/// </summary>
public interface IPeerStorageDbRepository
{
    /// <summary>
    /// The blob kept for <paramref name="peerNodeId"/>, or null.
    /// </summary>
    Task<StoredPeerBlob?> GetAsync(CompactPubKey peerNodeId);

    /// <summary>
    /// Every blob we keep.
    /// </summary>
    Task<IReadOnlyList<StoredPeerBlob>> GetAllAsync();

    /// <summary>
    /// Stages the peer's blob, replacing the one kept before (saved with the unit of work).
    /// </summary>
    Task UpsertAsync(StoredPeerBlob blob);

    /// <summary>
    /// Stages the removal of the peer's blob, if any (saved with the unit of work).
    /// </summary>
    Task DeleteAsync(CompactPubKey peerNodeId);
}