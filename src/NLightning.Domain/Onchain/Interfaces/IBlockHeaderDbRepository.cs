namespace NLightning.Domain.Onchain.Interfaces;

using Models;

/// <summary>
/// Persistence port for the ring of recently processed block headers (table <c>BlockHeaders</c>), used to detect
/// reorgs. Writes are staged and committed by <c>IUnitOfWork.SaveChangesAsync</c>.
/// </summary>
public interface IBlockHeaderDbRepository
{
    /// <summary>Stages a processed block (replacing a stored header at the same height).</summary>
    Task AddOrReplaceAsync(BlockHeaderModel header);

    /// <summary>The stored headers, lowest height first.</summary>
    Task<IReadOnlyList<BlockHeaderModel>> GetAllAsync();

    /// <summary>Stages deleting every header above <paramref name="height"/> (reorg).</summary>
    Task DeleteAboveAsync(uint height);

    /// <summary>Stages deleting every header below <paramref name="height"/> (ring pruning).</summary>
    Task DeleteBelowAsync(uint height);
}