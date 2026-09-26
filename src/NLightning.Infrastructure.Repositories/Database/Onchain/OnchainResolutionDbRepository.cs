using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Onchain;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Persistence.Contexts;
using Persistence.Entities.Onchain;

/// <summary>
/// Stores the funding spend of each closed channel and the outputs being resolved (BOLT 5 plan O1-T2). Writes are
/// staged on the unit of work; reads by key go through the change tracker, so they see what this unit of work staged.
/// </summary>
public class OnchainResolutionDbRepository : IOnchainResolutionDbRepository
{
    private readonly NLightningDbContext _context;

    public OnchainResolutionDbRepository(NLightningDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc />
    public async Task UpsertCloseAsync(ChannelCloseModel close)
    {
        ArgumentNullException.ThrowIfNull(close);

        var entity = await _context.ChannelCloses.FindAsync(close.ChannelId);
        if (entity is null)
        {
            _context.ChannelCloses.Add(new ChannelCloseEntity
            {
                ChannelId = close.ChannelId,
                Kind = (byte)close.Kind,
                CommitmentTxId = close.CommitmentTransactionId,
                CommitmentNumber = close.CommitmentNumber,
                SpentAtHeight = close.SpentAtHeight,
                BlockHash = close.BlockHash,
                CreatedAt = close.CreatedAt
            });
            return;
        }

        entity.Kind = (byte)close.Kind;
        entity.CommitmentTxId = close.CommitmentTransactionId;
        entity.CommitmentNumber = close.CommitmentNumber;
        entity.SpentAtHeight = close.SpentAtHeight;
        entity.BlockHash = close.BlockHash;
        entity.CreatedAt = close.CreatedAt;
        ReviveIfDeleted(entity);
    }

    /// <inheritdoc />
    public async Task<ChannelCloseModel?> GetCloseAsync(ChannelId channelId)
    {
        var entity = await _context.ChannelCloses.FindAsync(channelId);
        return entity is null || _context.Entry(entity).State == EntityState.Deleted ? null : MapClose(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChannelCloseModel>> GetClosesAsync()
    {
        var entities = await _context.ChannelCloses.AsNoTracking().ToListAsync();
        return entities.Select(MapClose).OrderBy(c => c.ChannelId.ToString(), StringComparer.Ordinal).ToList();
    }

    /// <inheritdoc />
    public async Task DeleteCloseAsync(ChannelId channelId)
    {
        var entity = await _context.ChannelCloses.FindAsync(channelId);
        if (entity is not null)
            _context.ChannelCloses.Remove(entity);
    }

    /// <inheritdoc />
    public async Task UpsertOutputAsync(OutputResolutionModel output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var entity = await _context.OutputResolutions.FindAsync(output.TransactionId, output.OutputIndex);
        var isNew = entity is null;
        entity ??= new OutputResolutionEntity
        {
            TransactionId = output.TransactionId,
            OutputIndex = output.OutputIndex,
            ChannelId = output.ChannelId,
            Descriptor = (byte)output.Descriptor,
            DescriptorData = [],
            State = (byte)output.State,
            CreatedAt = output.CreatedAt
        };

        entity.ChannelId = output.ChannelId;
        entity.Descriptor = (byte)output.Descriptor;
        entity.DescriptorData = output.DescriptorData.ToArray();
        entity.HtlcDirection = output.HtlcDirection is { } direction ? (byte)direction : null;
        entity.HtlcId = output.HtlcId;
        entity.State = (byte)output.State;
        entity.ResolvingTxId = output.ResolvingTransactionId;
        entity.WaitUntilHeight = output.WaitUntilHeight;
        entity.DeadlineHeight = output.DeadlineHeight;
        entity.ResolvedHeight = output.ResolvedHeight;
        entity.CreatedAt = output.CreatedAt;

        if (isNew)
            _context.OutputResolutions.Add(entity);
        else
            ReviveIfDeleted(entity);
    }

    /// <inheritdoc />
    public async Task<OutputResolutionModel?> GetOutputAsync(TxId transactionId, uint outputIndex)
    {
        var entity = await _context.OutputResolutions.FindAsync(transactionId, outputIndex);
        return entity is null || _context.Entry(entity).State == EntityState.Deleted ? null : MapOutput(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OutputResolutionModel>> GetOutputsByChannelIdAsync(ChannelId channelId)
    {
        var entities = await _context.OutputResolutions.AsNoTracking()
                                     .Where(o => o.ChannelId == channelId)
                                     .ToListAsync();
        return Ordered(entities);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OutputResolutionModel>> GetUnresolvedOutputsAsync()
    {
        const byte irrevocable = (byte)OutputResolutionState.Irrevocable;
        const byte ignored = (byte)OutputResolutionState.Ignored;
        var entities = await _context.OutputResolutions.AsNoTracking()
                                     .Where(o => o.State != irrevocable && o.State != ignored)
                                     .ToListAsync();
        return Ordered(entities);
    }

    private static List<OutputResolutionModel> Ordered(IEnumerable<OutputResolutionEntity> entities) =>
        entities.Select(MapOutput)
                .OrderBy(o => o.ChannelId.ToString(), StringComparer.Ordinal)
                .ThenBy(o => o.TransactionId.ToString(), StringComparer.Ordinal)
                .ThenBy(o => o.OutputIndex)
                .ToList();

    private static ChannelCloseModel MapClose(ChannelCloseEntity entity) =>
        new(entity.ChannelId, (ChannelCloseKind)entity.Kind, entity.CommitmentTxId, entity.CommitmentNumber,
            entity.SpentAtHeight, entity.BlockHash, entity.CreatedAt);

    private static OutputResolutionModel MapOutput(OutputResolutionEntity entity) =>
        new()
        {
            TransactionId = entity.TransactionId,
            OutputIndex = entity.OutputIndex,
            ChannelId = entity.ChannelId,
            Descriptor = (OutputDescriptorKind)entity.Descriptor,
            DescriptorData = entity.DescriptorData.ToArray(),
            HtlcDirection = entity.HtlcDirection is { } direction ? (HtlcDirection)direction : null,
            HtlcId = entity.HtlcId,
            State = (OutputResolutionState)entity.State,
            ResolvingTransactionId = entity.ResolvingTxId,
            WaitUntilHeight = entity.WaitUntilHeight,
            DeadlineHeight = entity.DeadlineHeight,
            ResolvedHeight = entity.ResolvedHeight,
            CreatedAt = entity.CreatedAt
        };

    /// <summary>A row removed earlier in the same unit of work and written again is an update, not a delete.</summary>
    private void ReviveIfDeleted(object entity)
    {
        var entry = _context.Entry(entity);
        if (entry.State == EntityState.Deleted)
            entry.State = EntityState.Modified;
    }
}