using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Bitcoin;

using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Persistence.Contexts;
using Persistence.Entities.Bitcoin;

/// <summary>The wallet's durable transaction history (NL-1187, table <c>WalletTransactions</c>).</summary>
public sealed class WalletTransactionDbRepository(NLightningDbContext context)
    : BaseDbRepository<WalletTransactionEntity>(context), IWalletTransactionDbRepository
{
    /// <inheritdoc />
    public async Task StageConfirmedAsync(WalletTransactionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var entity = await DbSet.FindAsync(record.TxId);
        if (entity is null)
        {
            Insert(new WalletTransactionEntity
            {
                TransactionId = record.TxId,
                RawTransaction = record.RawTransaction,
                BlockHeight = record.BlockHeight,
                BlockHash = record.BlockHash,
                Timestamp = record.Timestamp,
                OurOutputs = EncodeOutputs(record.OurOutputs),
                OurInputs = EncodeInputs(record.OurInputs),
                OwnershipSummary = record.OwnershipSummary
            });
            return;
        }

        // Ownership is a property of the transaction and the wallet, not of the block: keep what either description
        // found (a replayed block's spends no longer find the outputs its first pass removed)
        var outputs = DecodeOutputs(entity.OurOutputs).Union(record.OurOutputs).Order().ToList();
        var inputs = DecodeInputs(entity.OurInputs).ToDictionary(i => i.InputIndex);
        foreach (var input in record.OurInputs)
            inputs.TryAdd(input.InputIndex, input);

        entity.RawTransaction = record.RawTransaction;
        entity.BlockHeight = record.BlockHeight;
        entity.BlockHash = record.BlockHash;
        entity.Timestamp = record.Timestamp;
        entity.OurOutputs = EncodeOutputs(outputs);
        entity.OurInputs = EncodeInputs(inputs.Values.OrderBy(i => i.InputIndex));
        var summary = MergeSummary(entity.OwnershipSummary, record.OwnershipSummary);
        if (summary is not null)
        {
            var keys = summary.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part[..part.IndexOf(':', part.IndexOf(':') + 1)]).ToHashSet(StringComparer.Ordinal);
            // A legacy row may know inputs the replay cannot rediscover. Never promote a partial projection:
            // keep the raw fallback until a backfill supplies every retained ownership identity.
            if (outputs.Any(index => !keys.Contains($"o:{index}")) || inputs.Keys.Any(index => !keys.Contains($"i:{index}")))
                summary = null;
        }
        entity.OwnershipSummary = summary;
    }

    /// <inheritdoc />
    public async Task<int> UnconfirmAboveAsync(uint height)
    {
        var entities = await DbSet.Where(e => e.BlockHeight != null && e.BlockHeight > height).ToListAsync();
        foreach (var entity in entities)
        {
            entity.BlockHeight = null;
            entity.BlockHash = null;
        }

        return entities.Count;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WalletTransactionRecord>> GetUnconfirmedAsync(CancellationToken cancellationToken)
    {
        var entities = await DbSet.AsNoTracking()
                                  .Where(e => e.BlockHeight == null)
                                  .ToListAsync(cancellationToken);
        return entities.Select(ToRecord).ToList();
    }

    /// <inheritdoc />
    public async Task StageRemoveAsync(TxId txId)
    {
        if (await DbSet.FindAsync(txId) is { } entity)
            DbSet.Remove(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<TxId, uint?>> GetHeightsAsync(CancellationToken cancellationToken)
    {
        var rows = await DbSet.AsNoTracking()
                              .Select(e => new { e.TransactionId, e.BlockHeight })
                              .ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.TransactionId, r => r.BlockHeight);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WalletTransactionRecord>> GetHistoryAsync(uint startHeight, uint endHeight,
                                                                               bool includeUnconfirmed,
                                                                               CancellationToken cancellationToken)
    {
        var entities = await DbSet.AsNoTracking()
                                  .Where(e => (e.BlockHeight != null && e.BlockHeight >= startHeight
                                                                     && e.BlockHeight <= endHeight)
                                           || (includeUnconfirmed && e.BlockHeight == null))
                                  .ToListAsync(cancellationToken);
        return entities.Select(ToRecord).ToList();
    }

    public async Task<WalletTransactionRecord?> GetByIdAsync(TxId txId, CancellationToken cancellationToken)
    {
        var entity = await DbSet.FindAsync([txId], cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<WalletTransactionRecord>> GetByIdsAsync(IReadOnlyCollection<TxId> txIds, CancellationToken cancellationToken)
    {
        if (txIds.Count > 500) throw new ArgumentOutOfRangeException(nameof(txIds));
        var rows = await DbSet.AsNoTracking().Where(e => txIds.Contains(e.TransactionId)).ToListAsync(cancellationToken);
        return rows.Select(ToRecord).ToList();
    }

    public async Task<IReadOnlyList<WalletTransactionRecord>> GetHistoryPageAsync(uint startHeight, uint endHeight,
        bool includeUnconfirmed, int offset, int limit, CancellationToken cancellationToken)
    {
        if (offset < 0 || limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        var page = await DbSet.AsNoTracking()
            .Where(e => (e.BlockHeight != null && e.BlockHeight >= startHeight && e.BlockHeight <= endHeight)
                || (includeUnconfirmed && e.BlockHeight == null))
            .OrderBy(e => e.TransactionId).Skip(offset).Take(limit)
            .Select(e => new WalletTransactionEntity
            {
                TransactionId = e.TransactionId,
                BlockHeight = e.BlockHeight,
                BlockHash = e.BlockHash,
                Timestamp = e.Timestamp,
                OurOutputs = e.OurOutputs,
                OurInputs = e.OurInputs,
                OwnershipSummary = e.OwnershipSummary,
                RawTransaction = e.OwnershipSummary == null ? e.RawTransaction : Array.Empty<byte>()
            }).ToListAsync(cancellationToken);
        return page.Select(ToRecord).ToList();
    }

    public async Task<WalletHistoryRescanState?> GetRescanStateAsync(CancellationToken cancellationToken)
    {
        var e = await Context.WalletHistoryRescanStates.AsNoTracking().SingleOrDefaultAsync(e => e.Id == 1, cancellationToken);
        return e is null ? null : new(e.Generation, e.RequestedFromHeight, e.AvailableFromHeight, e.TargetHeight,
            e.CursorHeight, e.CursorHash, e.AddressCount, e.IsActive, e.IsPartial, e.Error);
    }

    public async Task StageRescanStateAsync(WalletHistoryRescanState state, CancellationToken cancellationToken)
    {
        var set = Context.WalletHistoryRescanStates;
        var e = await set.FindAsync([1], cancellationToken);
        if (e is null) { e = new WalletHistoryRescanStateEntity(); set.Add(e); }
        e.Generation = state.Generation; e.RequestedFromHeight = state.RequestedFromHeight;
        e.AvailableFromHeight = state.AvailableFromHeight; e.TargetHeight = state.TargetHeight;
        e.CursorHeight = state.CursorHeight; e.CursorHash = state.CursorHash; e.AddressCount = state.AddressCount;
        e.IsActive = state.IsActive; e.IsPartial = state.IsPartial; e.Error = state.Error;
    }

    public async Task<string?> GetLabelAsync(TxId txId, CancellationToken cancellationToken) =>
        (await Context.WalletTransactionLabels.AsNoTracking().SingleOrDefaultAsync(e => e.TransactionId == txId,
            cancellationToken))?.Label;

    public async Task<bool> StageLabelAsync(TxId txId, string label, bool overwrite, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (label.Length > 500) throw new ArgumentOutOfRangeException(nameof(label));
        var e = await Context.WalletTransactionLabels.FindAsync([txId], cancellationToken);
        if (e is not null && !overwrite) return false;
        if (e is null) Context.WalletTransactionLabels.Add(new WalletTransactionLabelEntity { TransactionId = txId, Label = label });
        else e.Label = label;
        return true;
    }

    private static string? MergeSummary(string? existing, string? incoming)
    {
        if (incoming is null) return existing;
        if (existing is null) return incoming;
        var parts = existing.Split(';', StringSplitOptions.RemoveEmptyEntries).ToDictionary(Key);
        foreach (var part in incoming.Split(';', StringSplitOptions.RemoveEmptyEntries)) parts[Key(part)] = part;
        return string.Join(';', parts.OrderBy(part => part.Key, StringComparer.Ordinal).Select(part => part.Value));
        static string Key(string part) => part[..part.IndexOf(':', part.IndexOf(':') + 1)];
    }

    private static WalletTransactionRecord ToRecord(WalletTransactionEntity e) =>
        new(e.TransactionId, e.RawTransaction, e.BlockHeight, e.BlockHash, e.Timestamp, DecodeOutputs(e.OurOutputs),
            DecodeInputs(e.OurInputs), e.OwnershipSummary);

    internal static string EncodeOutputs(IEnumerable<uint> outputs) =>
        string.Join(',', outputs.Select(o => o.ToString(CultureInfo.InvariantCulture)));

    internal static string EncodeInputs(IEnumerable<WalletTransactionInput> inputs) =>
        string.Join(',', inputs.Select(i => string.Create(CultureInfo.InvariantCulture,
                                                          $"{i.InputIndex}:{i.AmountSat}")));

    internal static List<uint> DecodeOutputs(string text) =>
        text.Length == 0
            ? []
            : text.Split(',').Select(o => uint.Parse(o, CultureInfo.InvariantCulture)).ToList();

    internal static List<WalletTransactionInput> DecodeInputs(string text)
    {
        if (text.Length == 0)
            return [];

        var inputs = new List<WalletTransactionInput>();
        foreach (var part in text.Split(','))
        {
            var separator = part.IndexOf(':');
            inputs.Add(new WalletTransactionInput(uint.Parse(part.AsSpan(0, separator), CultureInfo.InvariantCulture),
                                                  long.Parse(part.AsSpan(separator + 1),
                                                             CultureInfo.InvariantCulture)));
        }

        return inputs;
    }
}