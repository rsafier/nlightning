using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Onchain;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Persistence.Contexts;
using Persistence.Entities.Bitcoin;

/// <summary>
/// Stores the outpoints the chain monitor watches (BOLT 5 plan O0-T2), keyed by the outpoint.
/// </summary>
/// <remarks>
/// Writes are staged on the unit of work. <see cref="GetAsync"/> and the write methods go through the change tracker
/// (they see what this unit of work staged); <see cref="GetActiveAsync"/> reads what is saved.
/// </remarks>
public class WatchedOutpointDbRepository : BaseDbRepository<WatchedOutpointEntity>, IWatchedOutpointDbRepository
{
    private readonly NLightningDbContext _context;

    public WatchedOutpointDbRepository(NLightningDbContext context) : base(context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public void Add(WatchedOutpointModel watchedOutpoint)
    {
        ArgumentNullException.ThrowIfNull(watchedOutpoint);
        Insert(MapDomainToEntity(watchedOutpoint));
    }

    /// <inheritdoc />
    public async Task<WatchedOutpointModel?> GetAsync(TxId transactionId, uint outputIndex)
    {
        var entity = await DbSet.FindAsync(transactionId, outputIndex);
        return entity is null ? null : MapEntityToDomain(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WatchedOutpointModel>> GetActiveAsync()
    {
        const byte closed = (byte)ChannelState.Closed;
        const byte stale = (byte)ChannelState.Stale;
        var entities = await DbSet.AsNoTracking()
                                  .Where(o => !_context.Channels.Any(c => c.ChannelId == o.ChannelId
                                                                       && (c.State == closed || c.State == stale)))
                                  .ToListAsync();
        return entities.Select(MapEntityToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task MarkSpentAsync(TxId transactionId, uint outputIndex, TxId spendingTransactionId, uint height,
                                     Hash blockHash)
    {
        var entity = await DbSet.FindAsync(transactionId, outputIndex);
        if (entity is null)
            return;

        entity.SpentByTransactionId = spendingTransactionId;
        entity.SpentAtHeight = height;
        entity.SpentBlockHash = blockHash;
    }

    /// <inheritdoc />
    public async Task<int> ClearSpendsAboveAsync(uint height)
    {
        var entities = await DbSet.Where(o => o.SpentAtHeight != null && o.SpentAtHeight > height).ToListAsync();
        foreach (var entity in entities)
        {
            entity.SpentByTransactionId = null;
            entity.SpentAtHeight = null;
            entity.SpentBlockHash = null;
        }

        return entities.Count;
    }

    /// <inheritdoc />
    public async Task<WatchedOutpointModel?> AddFundingOutpointIfMissingAsync(ChannelId channelId,
                                                                              TxId fundingTransactionId)
    {
        var channel = await _context.Channels.AsNoTracking()
                                    .Where(c => c.ChannelId == channelId)
                                    .Select(c => new { c.FundingTxId, c.FundingOutputIndex, c.State })
                                    .FirstOrDefaultAsync();
        if (channel is null || channel.FundingTxId != fundingTransactionId
                            || channel.State is (byte)ChannelState.Closed or (byte)ChannelState.Stale)
            return null;

        if (await DbSet.FindAsync(fundingTransactionId, (uint)channel.FundingOutputIndex) is not null)
            return null;

        var watch = new WatchedOutpointModel(fundingTransactionId, channel.FundingOutputIndex, channelId,
                                             WatchedOutpointPurpose.FundingOutput);
        Add(watch);
        return watch;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WatchedOutpointModel>> AddMissingFundingOutpointsAsync()
    {
        const byte fundingCreated = (byte)ChannelState.V1FundingCreated;
        const byte closed = (byte)ChannelState.Closed;
        const byte stale = (byte)ChannelState.Stale;
        var channels = await _context.Channels.AsNoTracking()
                                     .Where(c => c.State >= fundingCreated && c.State != closed && c.State != stale)
                                     .Where(c => !DbSet.Any(o => o.TransactionId == c.FundingTxId
                                                              && o.OutputIndex == c.FundingOutputIndex))
                                     .Select(c => new { c.ChannelId, c.FundingTxId, c.FundingOutputIndex })
                                     .ToListAsync();

        var added = new List<WatchedOutpointModel>();
        foreach (var channel in channels)
        {
            // Skip a watch staged in this unit of work
            if (await DbSet.FindAsync(channel.FundingTxId, (uint)channel.FundingOutputIndex) is not null)
                continue;

            var watch = new WatchedOutpointModel(channel.FundingTxId, channel.FundingOutputIndex, channel.ChannelId,
                                                 WatchedOutpointPurpose.FundingOutput);
            Add(watch);
            added.Add(watch);
        }

        return added;
    }

    private static WatchedOutpointEntity MapDomainToEntity(WatchedOutpointModel model)
    {
        return new WatchedOutpointEntity
        {
            TransactionId = model.TransactionId,
            OutputIndex = model.OutputIndex,
            ChannelId = model.ChannelId,
            Purpose = (byte)model.Purpose,
            SpentByTransactionId = model.SpentByTransactionId,
            SpentAtHeight = model.SpentAtHeight,
            SpentBlockHash = model.SpentBlockHash,
            CreatedAt = model.CreatedAt
        };
    }

    private static WatchedOutpointModel MapEntityToDomain(WatchedOutpointEntity entity)
    {
        var model = new WatchedOutpointModel(entity.TransactionId, entity.OutputIndex, entity.ChannelId,
                                             (WatchedOutpointPurpose)entity.Purpose, entity.CreatedAt);
        if (entity is { SpentByTransactionId: { } spender, SpentAtHeight: { } height, SpentBlockHash: { } hash })
            model.MarkSpent(spender, height, hash);

        return model;
    }
}