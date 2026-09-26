using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Onchain;

using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Persistence.Contexts;
using Persistence.Entities.Bitcoin;

/// <summary>
/// Stores the transactions we broadcast with their raw bytes (BOLT 5 plan O0-T1), keyed by txid.
/// </summary>
/// <remarks>
/// Writes are staged on the unit of work. <see cref="GetByTransactionIdAsync"/> and the write methods go through the
/// change tracker (they see what this unit of work staged); <see cref="GetPendingAsync"/> reads what is saved.
/// </remarks>
public class BroadcastTransactionDbRepository
    : BaseDbRepository<BroadcastTransactionEntity>, IBroadcastTransactionDbRepository
{
    public BroadcastTransactionDbRepository(NLightningDbContext context) : base(context)
    {
    }

    /// <inheritdoc />
    public void Add(BroadcastTransactionModel transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        Insert(new BroadcastTransactionEntity
        {
            TransactionId = transaction.TransactionId,
            ChannelId = transaction.ChannelId,
            RawTransaction = transaction.RawTransaction.ToArray(),
            Purpose = (byte)transaction.Purpose,
            FeeratePerKw = transaction.FeeratePerKw,
            ReplacesTransactionId = transaction.ReplacesTransactionId,
            FirstBroadcastHeight = transaction.FirstBroadcastHeight,
            State = (byte)transaction.State,
            ConfirmedHeight = transaction.ConfirmedHeight,
            ConfirmedBlockHash = transaction.ConfirmedBlockHash,
            CreatedAt = transaction.CreatedAt
        });
    }

    /// <inheritdoc />
    public async Task<BroadcastTransactionModel?> GetByTransactionIdAsync(TxId transactionId)
    {
        var entity = await DbSet.FindAsync(transactionId);
        return entity is null ? null : MapEntityToDomain(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BroadcastTransactionModel>> GetPendingAsync()
    {
        const byte pending = (byte)BroadcastState.Pending;
        var entities = await DbSet.AsNoTracking()
                                  .Where(b => b.State == pending)
                                  .ToListAsync();
        return entities.OrderBy(b => b.CreatedAt).Select(MapEntityToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task MarkConfirmedAsync(TxId transactionId, uint height, Hash blockHash)
    {
        var entity = await DbSet.FindAsync(transactionId);
        if (entity is null || entity.State is (byte)BroadcastState.Replaced or (byte)BroadcastState.Abandoned)
            return;

        entity.State = (byte)BroadcastState.Confirmed;
        entity.ConfirmedHeight = height;
        entity.ConfirmedBlockHash = blockHash;
    }

    /// <inheritdoc />
    public async Task<int> UnconfirmAboveAsync(uint height)
    {
        const byte confirmed = (byte)BroadcastState.Confirmed;
        var entities = await DbSet.Where(b => b.State == confirmed && b.ConfirmedHeight > height).ToListAsync();
        foreach (var entity in entities)
        {
            entity.State = (byte)BroadcastState.Pending;
            entity.ConfirmedHeight = null;
            entity.ConfirmedBlockHash = null;
        }

        return entities.Count;
    }

    private static BroadcastTransactionModel MapEntityToDomain(BroadcastTransactionEntity entity)
    {
        return BroadcastTransactionModel.Restore(entity.TransactionId, entity.RawTransaction,
                                                 (BroadcastPurpose)entity.Purpose, entity.ChannelId,
                                                 (uint)entity.FeeratePerKw, entity.ReplacesTransactionId,
                                                 entity.FirstBroadcastHeight, (BroadcastState)entity.State,
                                                 entity.ConfirmedHeight, entity.ConfirmedBlockHash,
                                                 entity.CreatedAt);
    }
}