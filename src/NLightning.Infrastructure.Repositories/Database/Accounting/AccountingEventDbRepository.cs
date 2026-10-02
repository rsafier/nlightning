using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Accounting;

using Domain.Accounting.Enums;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Persistence.Contexts;
using Persistence.Entities.Accounting;

/// <summary>
/// The accounting feed's table (NL-602). Writes are staged on the unit of work.
/// </summary>
public class AccountingEventDbRepository : BaseDbRepository<AccountingEventEntity>, IAccountingEventDbRepository
{
    private const int DuplicateFlag = (int)AccountingEventFlags.Duplicate;

    public AccountingEventDbRepository(NLightningDbContext context) : base(context)
    {
    }

    /// <inheritdoc />
    public void Add(AccountingEventModel accountingEvent)
    {
        ArgumentNullException.ThrowIfNull(accountingEvent);
        if (accountingEvent.IsSealed)
            throw new ArgumentException("A new accounting event cannot be sealed", nameof(accountingEvent));

        Insert(new AccountingEventEntity
        {
            EventKey = accountingEvent.EventKey,
            Kind = (int)accountingEvent.Kind,
            OccurredAt = accountingEvent.OccurredAt,
            BlockHeight = accountingEvent.BlockHeight,
            ChannelId = accountingEvent.ChannelId,
            ShortChannelId = accountingEvent.ShortChannelId,
            PaymentHash = accountingEvent.PaymentHash,
            TxId = accountingEvent.TxId,
            OutputIndex = accountingEvent.OutputIndex,
            Counterparty = accountingEvent.Counterparty,
            AmountMsat = accountingEvent.AmountMsat,
            FeeMsat = accountingEvent.FeeMsat,
            Finality = (byte)accountingEvent.Finality,
            Flags = (int)accountingEvent.Flags,
            Details = AccountingDetailsCodec.Encode(accountingEvent.Details)
        });
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string eventKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventKey);

        if (DbSet.Local.Any(e => e.EventKey == eventKey && (e.Flags & DuplicateFlag) == 0))
            return true;

        return await DbSet.AsNoTracking()
                          .AnyAsync(e => e.EventKey == eventKey && (e.Flags & DuplicateFlag) == 0, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountingEventModel>> GetUnsealedAsync(
        int max, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);

        var entities = await DbSet.AsNoTracking()
                                  .Where(e => e.LedgerSeq == null && (e.Flags & DuplicateFlag) == 0)
                                  .OrderBy(e => e.Id)
                                  .Take(max)
                                  .ToListAsync(cancellationToken);
        return entities.Select(MapEntityToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task<AccountingChainTip> GetChainTipAsync(CancellationToken cancellationToken = default)
    {
        var tip = await DbSet.AsNoTracking()
                             .Where(e => e.LedgerSeq != null)
                             .OrderByDescending(e => e.LedgerSeq)
                             .Select(e => new { e.LedgerSeq, e.Hash })
                             .FirstOrDefaultAsync(cancellationToken);

        return tip is null ? AccountingChainTip.Genesis : new AccountingChainTip(tip.LedgerSeq!.Value, tip.Hash!);
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> GetSealedKeysAsync(IReadOnlyCollection<string> eventKeys,
                                                                CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventKeys);
        if (eventKeys.Count == 0)
            return new HashSet<string>(StringComparer.Ordinal);

        var keys = eventKeys.Distinct(StringComparer.Ordinal).ToList();
        var sealedKeys = await DbSet.AsNoTracking()
                                    .Where(e => e.LedgerSeq != null && keys.Contains(e.EventKey))
                                    .Select(e => e.EventKey)
                                    .ToListAsync(cancellationToken);
        return new HashSet<string>(sealedKeys, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async Task ApplySealsAsync(IReadOnlyList<AccountingSeal> seals,
                                      CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seals);
        if (seals.Count == 0)
            return;

        var ids = seals.Select(s => s.Id).ToList();
        var entities = await DbSet.Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, cancellationToken);
        foreach (var seal in seals)
        {
            if (!entities.TryGetValue(seal.Id, out var entity))
                throw new InvalidOperationException($"Accounting event {seal.Id} does not exist");
            if (entity.LedgerSeq is not null)
                throw new InvalidOperationException($"Accounting event {seal.Id} is already sealed");

            if (seal.IsDuplicate)
            {
                entity.Flags |= DuplicateFlag;
                continue;
            }

            entity.LedgerSeq = seal.LedgerSeq;
            entity.Hash = seal.Hash;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountingEventModel>> ListAsync(AccountingEventQuery query,
                                                                      CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.Take);

        var rows = DbSet.AsNoTracking().Where(e => e.LedgerSeq != null && e.LedgerSeq > query.AfterLedgerSeq);
        if (query.Kinds is { Count: > 0 } kinds)
        {
            var kindValues = kinds.Select(k => (int)k).ToList();
            rows = rows.Where(e => kindValues.Contains(e.Kind));
        }

        if (query.ChannelId is { } channelId)
            rows = rows.Where(e => e.ChannelId == channelId);

        if (query.Since is { } since)
            rows = rows.Where(e => e.OccurredAt >= since);

        if (query.Until is { } until)
            rows = rows.Where(e => e.OccurredAt < until);

        var entities = await rows.OrderBy(e => e.LedgerSeq).Take(query.Take).ToListAsync(cancellationToken);
        return entities.Select(MapEntityToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountingEventModel>> GetSealedRangeAsync(
        long fromLedgerSeq, int take, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);

        var entities = await DbSet.AsNoTracking()
                                  .Where(e => e.LedgerSeq != null && e.LedgerSeq >= fromLedgerSeq)
                                  .OrderBy(e => e.LedgerSeq)
                                  .Take(take)
                                  .ToListAsync(cancellationToken);
        return entities.Select(MapEntityToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountingEventModel>> GetAtOrAboveHeightAsync(
        uint height, IReadOnlyCollection<AccountingEventKind> kinds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        if (kinds.Count == 0)
            return [];

        var kindValues = kinds.Select(k => (int)k).Distinct().ToList();
        var saved = await DbSet.AsNoTracking()
                               .Where(e => e.BlockHeight != null && e.BlockHeight >= height
                                        && kindValues.Contains(e.Kind) && (e.Flags & DuplicateFlag) == 0)
                               .OrderBy(e => e.Id)
                               .ToListAsync(cancellationToken);
        var staged = GetStaged(e => e.BlockHeight is { } blockHeight && blockHeight >= height
                                 && kindValues.Contains(e.Kind));
        return saved.Concat(staged).Select(MapEntityToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountingEventModel>> GetByKeyPrefixAsync(
        string keyPrefix, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyPrefix);

        // LIKE is case-insensitive on SQLite: the ordinal check runs again on what comes back
        var saved = await DbSet.AsNoTracking()
                               .Where(e => e.EventKey.StartsWith(keyPrefix) && (e.Flags & DuplicateFlag) == 0)
                               .OrderBy(e => e.Id)
                               .ToListAsync(cancellationToken);
        var staged = GetStaged(e => e.EventKey.StartsWith(keyPrefix, StringComparison.Ordinal));
        return saved.Where(e => e.EventKey.StartsWith(keyPrefix, StringComparison.Ordinal)).Concat(staged)
                    .Select(MapEntityToDomain).ToList();
    }

    /// <summary>The rows added in this unit of work and not saved yet that match <paramref name="predicate"/>,
    /// duplicates excluded.</summary>
    private List<AccountingEventEntity> GetStaged(Func<AccountingEventEntity, bool> predicate) =>
        DbSet.Local.Where(e => DbSet.Entry(e).State == EntityState.Added && (e.Flags & DuplicateFlag) == 0
                            && predicate(e))
             .ToList();

    /// <inheritdoc />
    public async Task<AccountingEventModel?> GetByKeyAsync(string eventKey,
                                                           CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventKey);

        var saved = await DbSet.AsNoTracking()
                               .Where(e => e.EventKey == eventKey && (e.Flags & DuplicateFlag) == 0)
                               .OrderBy(e => e.Id)
                               .FirstOrDefaultAsync(cancellationToken);
        if (saved is not null)
            return MapEntityToDomain(saved);

        var staged = DbSet.Local.FirstOrDefault(e => e.EventKey == eventKey && (e.Flags & DuplicateFlag) == 0);
        return staged is null ? null : MapEntityToDomain(staged);
    }

    private static AccountingEventModel MapEntityToDomain(AccountingEventEntity entity)
    {
        return new AccountingEventModel
        {
            Id = entity.Id,
            EventKey = entity.EventKey,
            Kind = (AccountingEventKind)entity.Kind,
            OccurredAt = entity.OccurredAt,
            BlockHeight = entity.BlockHeight,
            ChannelId = entity.ChannelId,
            ShortChannelId = entity.ShortChannelId,
            PaymentHash = entity.PaymentHash,
            TxId = entity.TxId,
            OutputIndex = entity.OutputIndex,
            Counterparty = entity.Counterparty,
            AmountMsat = entity.AmountMsat,
            FeeMsat = entity.FeeMsat,
            Finality = (AccountingFinality)entity.Finality,
            Flags = (AccountingEventFlags)entity.Flags,
            Details = AccountingDetailsCodec.Decode(entity.Details),
            LedgerSeq = entity.LedgerSeq,
            Hash = entity.Hash
        };
    }
}