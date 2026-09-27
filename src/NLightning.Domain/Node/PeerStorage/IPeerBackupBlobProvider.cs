namespace NLightning.Domain.Node.PeerStorage;

/// <summary>
/// Builds the encrypted backup blob we ask peers to keep (<c>peer_storage</c>) and reads one they hand back
/// (<c>peer_storage_retrieval</c>). The blob format belongs to the implementation (BOLT 1 only requires it to be
/// encrypted with integrity and at most <see cref="PeerStorageConstants.MaxBlobLength"/> bytes, padded to that length).
/// </summary>
/// <remarks>
/// The default (<c>ChannelListPeerBackupBlobProvider</c>) stores the ids and peers of our open channels; the static
/// channel backup (SCB) lane replaces it by registering its own implementation.
/// </remarks>
public interface IPeerBackupBlobProvider
{
    /// <summary>
    /// Builds our current backup blob, or null when there is nothing to back up.
    /// </summary>
    Task<PeerBackupBlob?> CreateBlobAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Decrypts and parses a blob a peer handed back, or returns null when it is not ours (another node's, corrupted,
    /// or an unknown version).
    /// </summary>
    Task<PeerBackupContents?> TryReadBlobAsync(ReadOnlyMemory<byte> blob, CancellationToken cancellationToken = default);
}