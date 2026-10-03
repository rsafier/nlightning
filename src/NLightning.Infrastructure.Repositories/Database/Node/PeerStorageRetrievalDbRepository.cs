using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Node;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Node.PeerStorage;
using Persistence.Contexts;
using Persistence.Entities.Node;

/// <summary>
/// The latest <c>peer_storage_retrieval</c> of each peer (table <c>PeerStorageRetrievals</c>, migration
/// <c>AddPeerStorageRetrievals</c>, NL-432).
/// </summary>
public class PeerStorageRetrievalDbRepository : BaseDbRepository<PeerStorageRetrievalEntity>,
                                                IPeerStorageRetrievalDbRepository
{
    private const int ChannelIdLength = 32;

    public PeerStorageRetrievalDbRepository(NLightningDbContext context) : base(context)
    {
    }

    /// <inheritdoc />
    public async Task<StoredPeerRetrieval?> GetAsync(CompactPubKey peerNodeId)
    {
        var entity = await DbSet.AsNoTracking().FirstOrDefaultAsync(e => e.NodeId.Equals(peerNodeId));
        return entity is null ? null : Map(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredPeerRetrieval>> GetAllAsync()
    {
        var entities = await DbSet.AsNoTracking().ToListAsync();
        return entities.Select(Map).ToList();
    }

    /// <inheritdoc />
    public async Task UpsertAsync(StoredPeerRetrieval retrieval)
    {
        ArgumentNullException.ThrowIfNull(retrieval);

        var unknown = new byte[retrieval.UnknownChannelIds.Count * ChannelIdLength];
        for (var i = 0; i < retrieval.UnknownChannelIds.Count; i++)
            ((ReadOnlySpan<byte>)retrieval.UnknownChannelIds[i]).CopyTo(unknown.AsSpan(i * ChannelIdLength));

        var entity = await DbSet.FindAsync(retrieval.PeerNodeId);
        if (entity is null)
        {
            Insert(new PeerStorageRetrievalEntity
            {
                NodeId = retrieval.PeerNodeId,
                ReceivedAt = retrieval.ReceivedAt,
                Blob = retrieval.Blob.ToArray(),
                MatchesLastSent = retrieval.MatchesLastSent,
                UnknownChannelIds = unknown
            });
            return;
        }

        entity.ReceivedAt = retrieval.ReceivedAt;
        entity.Blob = retrieval.Blob.ToArray();
        entity.MatchesLastSent = retrieval.MatchesLastSent;
        entity.UnknownChannelIds = unknown;
    }

    private static StoredPeerRetrieval Map(PeerStorageRetrievalEntity entity)
    {
        var count = entity.UnknownChannelIds.Length / ChannelIdLength;
        var unknown = new List<ChannelId>(count);
        for (var i = 0; i < count; i++)
            unknown.Add(new ChannelId(entity.UnknownChannelIds.AsSpan(i * ChannelIdLength, ChannelIdLength)));

        return new StoredPeerRetrieval(entity.NodeId, entity.ReceivedAt, entity.Blob, entity.MatchesLastSent, unknown);
    }
}