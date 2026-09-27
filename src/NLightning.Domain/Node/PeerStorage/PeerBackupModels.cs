namespace NLightning.Domain.Node.PeerStorage;

using Channels.ValueObjects;
using Crypto.ValueObjects;

/// <summary>
/// Our encrypted backup blob and a fingerprint of what it holds.
/// </summary>
/// <param name="Blob">The bytes that go into <c>peer_storage</c>.</param>
/// <param name="Fingerprint">
/// Equal for two blobs that hold the same backup (the blob itself changes with every encryption), so an unchanged
/// backup is not sent again.
/// </param>
public sealed record PeerBackupBlob(byte[] Blob, string Fingerprint);

/// <summary>
/// One channel named in a backup blob.
/// </summary>
public sealed record PeerBackupChannel(ChannelId ChannelId, CompactPubKey PeerNodeId);

/// <summary>
/// What one of our backup blobs holds.
/// </summary>
/// <param name="CreatedAt">When the blob was built.</param>
/// <param name="Channels">The channels it names.</param>
public sealed record PeerBackupContents(DateTimeOffset CreatedAt, IReadOnlyList<PeerBackupChannel> Channels);

/// <summary>
/// A <c>peer_storage_retrieval</c> a peer sent us.
/// </summary>
/// <param name="PeerNodeId">The peer that handed the blob back.</param>
/// <param name="ReceivedAt">When it arrived.</param>
/// <param name="BlobLength">The blob's length in bytes.</param>
/// <param name="Contents">The blob's contents, or null when it is not one of ours (not decryptable).</param>
/// <param name="MatchesLastSent">
/// Whether it is the last blob we sent this peer since the start (null when we sent it none since the start).
/// </param>
/// <param name="UnknownChannels">
/// Channels the blob names that we have no record of: a sign of data loss, to be restored.
/// </param>
public sealed record PeerBackupRetrieval(CompactPubKey PeerNodeId, DateTimeOffset ReceivedAt, int BlobLength,
                                         PeerBackupContents? Contents, bool? MatchesLastSent,
                                         IReadOnlyList<PeerBackupChannel> UnknownChannels);

/// <summary>
/// The blob a peer asked us to keep (table <c>PeerStorageBlobs</c>).
/// </summary>
/// <param name="PeerNodeId">The peer that sent it.</param>
/// <param name="Blob">Its latest <c>peer_storage</c> blob.</param>
/// <param name="UpdatedAt">When we stored it.</param>
public sealed record StoredPeerBlob(CompactPubKey PeerNodeId, byte[] Blob, DateTimeOffset UpdatedAt);