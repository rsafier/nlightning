using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Node;

using Domain.Crypto.ValueObjects;
using Domain.Node.PeerStorage;
using Persistence.Contexts;
using Persistence.Entities.Node;

/// <summary>
/// The blobs peers asked us to keep (table <c>PeerStorageBlobs</c>, migration <c>AddPeerStorage</c>).
/// </summary>
public class PeerStorageDbRepository : BaseDbRepository<PeerStorageBlobEntity>, IPeerStorageDbRepository
{
    public PeerStorageDbRepository(NLightningDbContext context) : base(context)
    {
    }

    /// <inheritdoc />
    public async Task<StoredPeerBlob?> GetAsync(CompactPubKey peerNodeId)
    {
        var entity = await DbSet.AsNoTracking().FirstOrDefaultAsync(e => e.NodeId.Equals(peerNodeId));
        return entity is null ? null : Map(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredPeerBlob>> GetAllAsync()
    {
        var entities = await DbSet.AsNoTracking().ToListAsync();
        return entities.Select(Map).ToList();
    }

    /// <inheritdoc />
    public async Task UpsertAsync(StoredPeerBlob blob)
    {
        ArgumentNullException.ThrowIfNull(blob);

        var entity = await DbSet.FindAsync(blob.PeerNodeId);
        if (entity is null)
        {
            Insert(new PeerStorageBlobEntity
            {
                NodeId = blob.PeerNodeId,
                Blob = blob.Blob.ToArray(),
                UpdatedAt = blob.UpdatedAt
            });
            return;
        }

        entity.Blob = blob.Blob.ToArray();
        entity.UpdatedAt = blob.UpdatedAt;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(CompactPubKey peerNodeId)
    {
        var entity = await DbSet.FindAsync(peerNodeId);
        if (entity is not null)
            Delete(entity);
    }

    private static StoredPeerBlob Map(PeerStorageBlobEntity entity) =>
        new(entity.NodeId, entity.Blob, entity.UpdatedAt);
}