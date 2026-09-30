namespace NLightning.Domain.Node.PeerStorage;

using Bitcoin.ValueObjects;
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
/// <param name="ChannelId">The channel id.</param>
/// <param name="PeerNodeId">The peer's node id.</param>
/// <param name="FundingTxId">The channel's current funding outpoint's txid (it moves with a locked splice, and with the
/// RBF of an unconfirmed dual-funded open), or null in a blob that predates it (version 1). Splicing plan SP2-0; written
/// by lane SP2-E in blob version 2, so the blob's fingerprint changes and it is sent again after a splice.</param>
/// <param name="FundingOutputIndex">The current funding output index, with <paramref name="FundingTxId"/>.</param>
/// <param name="LocalFundingKeyIndex">Our funding key index of the current funding (splicing plan D5: 0 before any
/// splice), with <paramref name="FundingTxId"/>.</param>
public sealed record PeerBackupChannel(
    ChannelId ChannelId,
    CompactPubKey PeerNodeId,
    TxId? FundingTxId = null,
    ushort? FundingOutputIndex = null,
    uint? LocalFundingKeyIndex = null);

/// <summary>
/// What one of our backup blobs holds.
/// </summary>
/// <param name="CreatedAt">When the blob was built.</param>
/// <param name="Channels">The channels it names.</param>
/// <param name="Fingerprint">
/// The <see cref="PeerBackupBlob.Fingerprint"/> the blob was built with (null when the provider cannot tell), so a
/// blob handed back that holds our current backup is not sent again.
/// </param>
public sealed record PeerBackupContents(DateTimeOffset CreatedAt, IReadOnlyList<PeerBackupChannel> Channels,
                                        string? Fingerprint = null);

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

/// <summary>
/// The latest <c>peer_storage_retrieval</c> of a peer as it is kept (table <c>PeerStorageRetrievals</c>, NL-432).
/// </summary>
/// <param name="PeerNodeId">The peer that handed the blob back.</param>
/// <param name="ReceivedAt">When it arrived.</param>
/// <param name="Blob">The blob exactly as the peer sent it (read again with <see cref="IPeerBackupBlobProvider"/>).</param>
/// <param name="MatchesLastSent">
/// Whether it was the last blob the node sent this peer in the process that received it (null when it had sent none).
/// </param>
/// <param name="UnknownChannelIds">
/// The channels the blob named that the node had no record of when it arrived (a sign of data loss).
/// </param>
public sealed record StoredPeerRetrieval(CompactPubKey PeerNodeId, DateTimeOffset ReceivedAt, byte[] Blob,
                                         bool? MatchesLastSent, IReadOnlyList<ChannelId> UnknownChannelIds);

/// <summary>
/// One channel of a retrieved backup, with whether the node knows it now.
/// </summary>
/// <param name="ChannelId">The channel.</param>
/// <param name="PeerNodeId">Its peer, as the backup names it.</param>
/// <param name="UnknownWhenReceived">True when the node had no record of it when the retrieval arrived.</param>
/// <param name="KnownNow">True when the node has a record of it now (e.g. restored from a static channel backup).</param>
public sealed record PeerBackupChannelStatus(ChannelId ChannelId, CompactPubKey PeerNodeId, bool UnknownWhenReceived,
                                             bool KnownNow);

/// <summary>
/// A peer's latest <c>peer_storage_retrieval</c>, read again for the operator (<c>listpeerstorage</c>, NL-432).
/// </summary>
/// <param name="PeerNodeId">The peer that handed the blob back.</param>
/// <param name="ReceivedAt">When it arrived.</param>
/// <param name="Blob">The blob as the peer sent it.</param>
/// <param name="Contents">What the blob holds, or null when it is not one of ours.</param>
/// <param name="MatchesLastSent">See <see cref="StoredPeerRetrieval.MatchesLastSent"/>.</param>
/// <param name="Channels">The channels the blob names, each with whether the node knows it now.</param>
/// <param name="Persisted">False when the row could not be written yet (it is retried at the next round).</param>
public sealed record PeerStorageRetrievalReport(CompactPubKey PeerNodeId, DateTimeOffset ReceivedAt, byte[] Blob,
                                                PeerBackupContents? Contents, bool? MatchesLastSent,
                                                IReadOnlyList<PeerBackupChannelStatus> Channels, bool Persisted)
{
    /// <summary>The channels the blob names that the node has no record of now: what is left to restore.</summary>
    public IEnumerable<PeerBackupChannelStatus> StillUnknown => Channels.Where(c => !c.KnownNow);
}

/// <summary>
/// A peer's refusals of our backup blob for its size (<c>warning</c>, NL-559): the peer keeps nothing of ours until a
/// blob within its limit reaches it, so every refusal is counted for the operator (<c>listpeerstorage</c>).
/// </summary>
/// <param name="PeerNodeId">The peer that refused.</param>
/// <param name="Count">How many of its refusals arrived since the start.</param>
/// <param name="AcceptedLimitBytes">
/// The blob length the peer accepts: what its warning named, or the default when it named nothing. Only ever lowered
/// by later refusals.
/// </param>
/// <param name="LastRefusedBlobLength">The length of the blob the peer refused last.</param>
/// <param name="LastRefusalAt">When the last refusal arrived.</param>
public sealed record PeerStorageRefusalReport(CompactPubKey PeerNodeId, int Count, int AcceptedLimitBytes,
                                              int LastRefusedBlobLength, DateTimeOffset LastRefusalAt);