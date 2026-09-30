using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Channels.ValueObjects;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;

/// <summary>
/// Response for ListPeerStorage (ClientCommand 32, NL-432): the latest <c>peer_storage_retrieval</c> of each peer and
/// the blobs we keep for our peers.
/// </summary>
[MessagePackObject]
public sealed class ListPeerStorageIpcResponse
{
    /// <summary>The latest retrieval of each peer, ordered by arrival.</summary>
    [Key(0)] public required List<PeerStorageRetrievalIpcInfo> Retrievals { get; init; }

    /// <summary>The blobs we keep for our peers (as a provider).</summary>
    [Key(1)] public required List<StoredPeerBlobIpcInfo> StoredBlobs { get; init; }

    /// <summary>True when our backups go to no peer until the restart (possible data loss).</summary>
    [Key(2)] public bool BackupsHeldForDataLoss { get; init; }

    /// <summary>The peers that refused our backup blob for its size (NL-559).</summary>
    [Key(3)] public required List<PeerRefusalIpcInfo> Refusals { get; init; }

    public static ListPeerStorageIpcResponse FromClientResponse(ListPeerStorageClientResponse clientResponse)
    {
        ArgumentNullException.ThrowIfNull(clientResponse);
        return new ListPeerStorageIpcResponse
        {
            Retrievals = clientResponse.Retrievals.Select(r => new PeerStorageRetrievalIpcInfo
            {
                PeerNodeId = r.PeerNodeId,
                ReceivedAt = r.ReceivedAt,
                BlobLength = r.Blob.Length,
                IsOurs = r.Contents is not null,
                BackupCreatedAt = r.Contents?.CreatedAt,
                MatchesLastSent = r.MatchesLastSent,
                Persisted = r.Persisted,
                Channels = r.Channels.Select(c => new PeerStorageChannelIpcInfo
                {
                    ChannelId = c.ChannelId,
                    PeerNodeId = c.PeerNodeId,
                    UnknownWhenReceived = c.UnknownWhenReceived,
                    KnownNow = c.KnownNow
                }).ToList(),
                Blob = clientResponse.IncludesBlobs ? r.Blob : null
            }).ToList(),
            StoredBlobs = clientResponse.StoredBlobs.Select(b => new StoredPeerBlobIpcInfo
            {
                PeerNodeId = b.PeerNodeId,
                BlobLength = b.Blob.Length,
                UpdatedAt = b.UpdatedAt
            }).ToList(),
            BackupsHeldForDataLoss = clientResponse.BackupsHeldForDataLoss,
            Refusals = clientResponse.Refusals.Select(r => new PeerRefusalIpcInfo
            {
                PeerNodeId = r.PeerNodeId,
                Count = r.Count,
                AcceptedLimitBytes = r.AcceptedLimitBytes,
                LastRefusedBlobLength = r.LastRefusedBlobLength,
                LastRefusalAt = r.LastRefusalAt
            }).ToList()
        };
    }
}

/// <summary>One peer's latest retrieval in a <see cref="ListPeerStorageIpcResponse"/>.</summary>
[MessagePackObject]
public sealed class PeerStorageRetrievalIpcInfo
{
    [Key(0)] public required CompactPubKey PeerNodeId { get; init; }
    [Key(1)] public required DateTimeOffset ReceivedAt { get; init; }
    [Key(2)] public int BlobLength { get; init; }

    /// <summary>True when the blob is one of our backups (it decrypts with our key).</summary>
    [Key(3)] public bool IsOurs { get; init; }

    /// <summary>When our backup in it was built; null when it is not ours.</summary>
    [Key(4)] public DateTimeOffset? BackupCreatedAt { get; init; }

    /// <summary>Whether it was the last blob we sent the peer (null when we had sent none in that process).</summary>
    [Key(5)] public bool? MatchesLastSent { get; init; }

    /// <summary>False when the row is not in the database yet (the write is retried).</summary>
    [Key(6)] public bool Persisted { get; init; }

    /// <summary>The channels our backup names.</summary>
    [Key(7)] public required List<PeerStorageChannelIpcInfo> Channels { get; init; }

    /// <summary>The blob as the peer sent it; only when asked for.</summary>
    [Key(8)] public byte[]? Blob { get; init; }
}

/// <summary>One channel named in a retrieved backup.</summary>
[MessagePackObject]
public sealed class PeerStorageChannelIpcInfo
{
    [Key(0)] public required ChannelId ChannelId { get; init; }
    [Key(1)] public required CompactPubKey PeerNodeId { get; init; }

    /// <summary>We had no record of it when the retrieval arrived.</summary>
    [Key(2)] public bool UnknownWhenReceived { get; init; }

    /// <summary>We have a record of it now.</summary>
    [Key(3)] public bool KnownNow { get; init; }
}

/// <summary>A blob we keep for a peer.</summary>
[MessagePackObject]
public sealed class StoredPeerBlobIpcInfo
{
    [Key(0)] public required CompactPubKey PeerNodeId { get; init; }
    [Key(1)] public int BlobLength { get; init; }
    [Key(2)] public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>A peer's refusals of our backup blob for its size (NL-559).</summary>
[MessagePackObject]
public sealed class PeerRefusalIpcInfo
{
    [Key(0)] public required CompactPubKey PeerNodeId { get; init; }

    /// <summary>How many of its refusals arrived since the start.</summary>
    [Key(1)] public int Count { get; init; }

    /// <summary>The blob length the peer accepts (what its warning named, or the default).</summary>
    [Key(2)] public int AcceptedLimitBytes { get; init; }

    /// <summary>The length of the blob the peer refused last.</summary>
    [Key(3)] public int LastRefusedBlobLength { get; init; }

    [Key(4)] public required DateTimeOffset LastRefusalAt { get; init; }
}