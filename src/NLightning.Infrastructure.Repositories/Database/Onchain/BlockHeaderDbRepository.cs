using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Onchain;

using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Persistence.Contexts;
using Persistence.Entities.Bitcoin;

/// <summary>
/// Stores the ring of recently processed block headers (BOLT 5 plan O0-T3), keyed by height.
/// </summary>
public class BlockHeaderDbRepository : BaseDbRepository<BlockHeaderEntity>, IBlockHeaderDbRepository
{
    public BlockHeaderDbRepository(NLightningDbContext context) : base(context)
    {
    }

    /// <inheritdoc />
    public async Task AddOrReplaceAsync(BlockHeaderModel header)
    {
        ArgumentNullException.ThrowIfNull(header);

        var entity = await DbSet.FindAsync(header.Height);
        if (entity is null)
        {
            Insert(new BlockHeaderEntity
            {
                Height = header.Height,
                BlockHash = header.BlockHash,
                PreviousBlockHash = header.PreviousBlockHash
            });
            return;
        }

        entity.BlockHash = header.BlockHash;
        entity.PreviousBlockHash = header.PreviousBlockHash;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BlockHeaderModel>> GetAllAsync()
    {
        var entities = await DbSet.AsNoTracking().OrderBy(h => h.Height).ToListAsync();
        return entities.Select(e => new BlockHeaderModel(e.Height, e.BlockHash, e.PreviousBlockHash)).ToList();
    }

    /// <inheritdoc />
    public async Task DeleteAboveAsync(uint height)
    {
        DeleteRange(await DbSet.Where(h => h.Height > height).ToListAsync());
    }

    /// <inheritdoc />
    public async Task DeleteBelowAsync(uint height)
    {
        DeleteRange(await DbSet.Where(h => h.Height < height).ToListAsync());
    }
}