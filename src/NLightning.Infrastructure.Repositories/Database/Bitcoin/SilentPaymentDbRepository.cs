using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Bitcoin;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Persistence.Contexts;
using Persistence.Entities.Bitcoin;

public sealed class SilentPaymentDbRepository(NLightningDbContext context) : ISilentPaymentDbRepository
{
    public async Task<SilentPaymentOutputModel?> GetOutputAsync(TxId transactionId, uint index,
        CancellationToken cancellationToken = default)
    {
        var entity = await context.SilentPaymentOutputs.FindAsync([transactionId, index], cancellationToken);
        return entity is null ? null : MapEntityToModel(entity);
    }

    public async Task<IReadOnlyList<SilentPaymentOutputModel>> GetOutputsAsync(CancellationToken cancellationToken = default) =>
        (await context.SilentPaymentOutputs.AsNoTracking().ToListAsync(cancellationToken)).Select(MapEntityToModel).ToArray();

    public async Task<IReadOnlyList<SilentPaymentOutputModel>> GetOutputsAboveHeightAsync(uint height,
        CancellationToken cancellationToken = default) =>
        (await context.SilentPaymentOutputs.Where(e => e.BlockHeight > height).AsNoTracking()
            .ToListAsync(cancellationToken)).Select(MapEntityToModel).ToArray();

    public async Task UpsertOutputAsync(SilentPaymentOutputModel output, CancellationToken cancellationToken = default)
    {
        if (output.OutputKey.Length != 32 || output.Tweak.Length != 32 || output.AmountSats < 0
            || output.SpentByTransactionId.HasValue != output.SpentAtHeight.HasValue)
            throw new ArgumentException("Invalid silent payment output.", nameof(output));
        var entity = await context.SilentPaymentOutputs.FindAsync([output.TransactionId, output.Index], cancellationToken);
        if (entity is null)
            context.SilentPaymentOutputs.Add(MapModelToEntity(output));
        else
            context.Entry(entity).CurrentValues.SetValues(MapModelToEntity(output));
    }

    public async Task SetSpentAsync(TxId transactionId, uint index, TxId? spentByTransactionId,
        uint? spentAtHeight, CancellationToken cancellationToken = default)
    {
        if (spentByTransactionId.HasValue != spentAtHeight.HasValue)
            throw new ArgumentException("A confirmed spend requires both transaction id and height.");
        var entity = await context.SilentPaymentOutputs.FindAsync([transactionId, index], cancellationToken)
            ?? throw new KeyNotFoundException("Unknown silent payment output.");
        entity.SpentByTransactionId = spentByTransactionId;
        entity.SpentAtHeight = spentAtHeight;
    }

    public async Task DeleteOutputsAboveHeightAsync(uint height, CancellationToken cancellationToken = default)
    {
        var entities = await context.SilentPaymentOutputs.Where(e => e.BlockHeight > height).ToListAsync(cancellationToken);
        context.SilentPaymentOutputs.RemoveRange(entities);
        foreach (var entry in context.ChangeTracker.Entries<SilentPaymentOutputEntity>().ToArray())
            if (entry.State == EntityState.Added && entry.Entity.BlockHeight > height)
                entry.State = EntityState.Detached;
    }

    public async Task<IReadOnlyList<SilentPaymentLabelModel>> GetLabelsAsync(CancellationToken cancellationToken = default) =>
        (await context.SilentPaymentLabels.OrderBy(e => e.M).AsNoTracking().ToListAsync(cancellationToken))
            .Select(e => new SilentPaymentLabelModel(e.M, e.Name, e.CreatedAtHeight)).ToArray();

    public void AddLabel(SilentPaymentLabelModel label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label.Name);
        if (label.Name.Length > 256)
            throw new ArgumentException("A silent payment label name cannot exceed 256 characters.", nameof(label));
        context.SilentPaymentLabels.Add(new SilentPaymentLabelEntity
        {
            M = label.M, Name = label.Name, CreatedAtHeight = label.CreatedAtHeight
        });
    }

    public async Task<SilentPaymentScanState?> GetScanStateAsync(CancellationToken cancellationToken = default)
    {
        var entity = await context.SilentPaymentScanState.FindAsync([(byte)0], cancellationToken);
        return entity is null ? null : new SilentPaymentScanState(entity.BirthdayHeight, entity.LiveFromHeight,
            entity.RescanCursorHeight, entity.RescanCursorHash, entity.RescanTargetHeight, entity.PrevoutSource,
            entity.LiveCursorHeight, entity.LiveCursorHash, entity.RecoveryLabelCount);
    }

    public async Task SetScanStateAsync(SilentPaymentScanState state, CancellationToken cancellationToken = default)
    {
        if (state.RescanCursorHash.HasValue && !state.RescanCursorHeight.HasValue
            || state.LiveCursorHash.HasValue && !state.LiveCursorHeight.HasValue)
            throw new ArgumentException("A cursor hash requires a corresponding height.", nameof(state));
        var replacement = new SilentPaymentScanStateEntity
        {
            Id = 0, BirthdayHeight = state.BirthdayHeight, LiveFromHeight = state.LiveFromHeight,
            RescanCursorHeight = state.RescanCursorHeight, RescanCursorHash = state.RescanCursorHash,
            RescanTargetHeight = state.RescanTargetHeight, PrevoutSource = state.PrevoutSource,
            LiveCursorHeight = state.LiveCursorHeight, LiveCursorHash = state.LiveCursorHash,
            RecoveryLabelCount = state.RecoveryLabelCount
        };
        var existing = await context.SilentPaymentScanState.FindAsync([(byte)0], cancellationToken);
        if (existing is null) context.SilentPaymentScanState.Add(replacement);
        else context.Entry(existing).CurrentValues.SetValues(replacement);
    }

    internal static SilentPaymentOutputModel MapEntityToModel(SilentPaymentOutputEntity entity) =>
        new(entity.TransactionId, entity.Index, entity.OutputKey.ToArray(), entity.Tweak.ToArray(), entity.Label,
            entity.AmountSats, entity.BlockHeight, entity.BlockHash, entity.SpentByTransactionId, entity.Ignored,
            entity.SpentAtHeight);

    private static SilentPaymentOutputEntity MapModelToEntity(SilentPaymentOutputModel output) => new()
    {
        TransactionId = output.TransactionId, Index = output.Index, OutputKey = output.OutputKey.ToArray(),
        Tweak = output.Tweak.ToArray(), Label = output.Label, AmountSats = output.AmountSats,
        BlockHeight = output.BlockHeight, BlockHash = output.BlockHash,
        SpentByTransactionId = output.SpentByTransactionId, Ignored = output.Ignored, SpentAtHeight = output.SpentAtHeight
    };
}