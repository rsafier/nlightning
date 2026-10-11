using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Node;

using Domain.Crypto.ValueObjects;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Persistence.Contexts;
using Persistence.Entities.Node;

public class PeerDbRepository : BaseDbRepository<PeerEntity>, IPeerDbRepository
{
    public PeerDbRepository(NLightningDbContext context) : base(context)
    {
    }

    public async Task AddOrUpdateAsync(PeerModel peerModel)
    {
        var peerEntity = MapDomainToEntity(peerModel);

        var existingEntity = await GetByIdAsync(peerEntity.NodeId);
        if (existingEntity is null)
        {
            Insert(peerEntity);
        }
        else
        {
            Update(peerEntity);
        }
    }

    public void Update(PeerModel peerModel)
    {
        var peerEntity = MapDomainToEntity(peerModel);
        base.Update(peerEntity);
    }

    public async Task<IEnumerable<PeerModel>> GetAllAsync()
    {
        var peerEntities = await Get().ToListAsync();

        return peerEntities.Select(MapEntityToDomain);
    }

    public async Task<PeerModel?> GetByNodeIdAsync(CompactPubKey nodeId)
    {
        var peerEntity = await GetByIdAsync(nodeId);
        if (peerEntity == null)
            return null;

        return MapEntityToDomain(peerEntity);
    }

    /// <remarks>
    /// Only <c>LastSeenAt</c> is written (the row is tracked, so the save updates the changed column alone): a full-row
    /// update from this read raced the connect paths' saves of the peer's address and wrote the row it had read back
    /// over them, e.g. an inbound-only row over the announced address a dial had just saved (NL-1386).
    /// </remarks>
    public async Task UpdatePeerLastSeenAsync(CompactPubKey peerCompactPubKey)
    {
        var existingPeer = await GetByIdAsync(peerCompactPubKey, asNoTracking: false);
        existingPeer?.LastSeenAt = DateTime.UtcNow;
    }

    private static PeerEntity MapDomainToEntity(PeerModel peerModel)
    {
        return new PeerEntity
        {
            NodeId = peerModel.NodeId,
            Host = peerModel.Host,
            Port = peerModel.Port,
            Type = peerModel.Type,
            LastSeenAt = peerModel.LastSeenAt,
            IsInboundOnly = peerModel.IsInboundOnly
        };
    }

    private static PeerModel MapEntityToDomain(PeerEntity peerEntity)
    {
        return new PeerModel(peerEntity.NodeId, peerEntity.Host, peerEntity.Port, peerEntity.Type)
        {
            LastSeenAt = peerEntity.LastSeenAt,
            IsInboundOnly = peerEntity.IsInboundOnly
        };
    }
}