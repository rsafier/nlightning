using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Bitcoin;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.ValueObjects;
using Persistence.Contexts;
using Persistence.Entities.Bitcoin;

public class WatchedTransactionDbRepository(NLightningDbContext context)
    : BaseDbRepository<WatchedTransactionEntity>(context), IWatchedTransactionDbRepository
{
    private readonly NLightningDbContext _context = context;

    public void Add(WatchedTransactionModel watchedTransactionModel)
    {
        var watchedTransactionEntity = MapDomainToEntity(watchedTransactionModel);

        if (watchedTransactionEntity.CreatedAt.Equals(DateTime.MinValue))
            watchedTransactionEntity.CreatedAt = DateTime.UtcNow;

        Insert(watchedTransactionEntity);
    }

    public void Update(WatchedTransactionModel watchedTransactionModel)
    {
        var watchedTransactionEntity = MapDomainToEntity(watchedTransactionModel);
        Update(watchedTransactionEntity);

        // The model does not carry the creation time: keep the stored one
        var entry = _context.Entry(DbSet.Local.FirstOrDefault(e => e.TransactionId == watchedTransactionEntity
                                                                                         .TransactionId)
                                ?? watchedTransactionEntity);
        if (entry.State == EntityState.Modified)
            entry.Property(e => e.CreatedAt).IsModified = false;
    }

    /// <inheritdoc />
    public async Task<int> ResetPendingFirstSeenAboveAsync(uint height)
    {
        var entities = await DbSet.Where(x => x.CompletedAt == null && x.FirstSeenAtHeight != null
                                           && x.FirstSeenAtHeight > height)
                                  .ToListAsync();
        foreach (var entity in entities)
        {
            entity.FirstSeenAtHeight = null;
            entity.TransactionIndex = null;
        }

        return entities.Count;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WatchedTransactionModel>> GetCompletedFirstSeenAboveAsync(uint height)
    {
        var entities = await DbSet.AsNoTracking()
                                  .Where(x => x.CompletedAt != null && x.FirstSeenAtHeight != null
                                           && x.FirstSeenAtHeight > height)
                                  .ToListAsync();
        return entities.Select(MapEntityToDomain).ToList();
    }

    public async Task<IEnumerable<WatchedTransactionModel>> GetAllPendingAsync()
    {
        var entities = await DbSet
                            .AsNoTracking()
                            .Where(x => x.CompletedAt == null)
                            .ToListAsync();

        return entities.Select(entity => MapEntityToDomain(entity));
    }

    public async Task<WatchedTransactionModel?> GetByTransactionIdAsync(TxId transactionId)
    {
        var entity = await GetByIdAsync(transactionId);
        return entity == null ? null : MapEntityToDomain(entity);
    }

    private static WatchedTransactionEntity MapDomainToEntity(WatchedTransactionModel watchedTransactionModel)
    {
        return new WatchedTransactionEntity
        {
            ChannelId = watchedTransactionModel.ChannelId,
            TransactionId = watchedTransactionModel.TransactionId,
            RequiredDepth = watchedTransactionModel.RequiredDepth,
            FirstSeenAtHeight = watchedTransactionModel.FirstSeenAtHeight,
            TransactionIndex = watchedTransactionModel.TransactionIndex,
            CompletedAt = watchedTransactionModel.IsCompleted ? DateTime.UtcNow : null
        };
    }

    private static WatchedTransactionModel MapEntityToDomain(WatchedTransactionEntity entity)
    {
        var model = new WatchedTransactionModel(entity.ChannelId, entity.TransactionId, entity.RequiredDepth);

        if (entity.CompletedAt.HasValue)
            model.MarkAsCompleted();

        if (entity.FirstSeenAtHeight.HasValue && entity.TransactionIndex.HasValue)
            model.SetHeightAndIndex(entity.FirstSeenAtHeight.Value, entity.TransactionIndex.Value);

        return model;
    }
}