using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.LiquidityAds;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.LiquidityAds.Enums;
using Domain.LiquidityAds.Interfaces;
using Domain.LiquidityAds.Models;
using Persistence.Contexts;
using Persistence.Entities.LiquidityAds;

/// <summary>
/// Stores the liquidity purchases we made and sold (NL-850 LA3, migration <c>AddLiquidityPurchases</c>).
/// </summary>
/// <remarks>
/// Writes are staged on the unit of work; the context's save assigns the database ids, which this repository copies
/// to the models it added (<see cref="LiquidityPurchaseModel.AssignId"/>). Reads see what is saved.
/// </remarks>
public class LiquidityPurchaseDbRepository : BaseDbRepository<LiquidityPurchaseEntity>, ILiquidityPurchaseDbRepository
{
    private const byte SellerRole = (byte)LiquidityPurchaseRole.Seller;
    private const byte PendingStatus = (byte)LiquidityPurchaseStatus.Pending;
    private const byte ActiveStatus = (byte)LiquidityPurchaseStatus.Active;

    // The purchases added through this repository whose save has not assigned their id yet
    private readonly List<(LiquidityPurchaseModel Model, LiquidityPurchaseEntity Entity)> _added = [];
    private readonly NLightningDbContext _context;

    public LiquidityPurchaseDbRepository(NLightningDbContext context) : base(context)
    {
        _context = context;
        _context.SavedChanges += OnSavedChanges;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The purchase was saved or added already.</exception>
    public void Add(LiquidityPurchaseModel purchase)
    {
        ArgumentNullException.ThrowIfNull(purchase);
        if (purchase.Id != 0)
            throw new InvalidOperationException($"Purchase {purchase.Id} is already stored.");
        if (_added.Exists(a => ReferenceEquals(a.Model, purchase)))
            throw new InvalidOperationException("The purchase is already staged.");

        var entity = MapDomainToEntity(purchase);
        Insert(entity);
        _added.Add((purchase, entity));
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The purchase was neither saved nor added through this
    /// repository.</exception>
    public void Update(LiquidityPurchaseModel purchase)
    {
        ArgumentNullException.ThrowIfNull(purchase);

        var stagedIndex = _added.FindIndex(a => ReferenceEquals(a.Model, purchase));
        if (stagedIndex >= 0)
        {
            CopyMutableFields(purchase, _added[stagedIndex].Entity);
            return;
        }

        if (purchase.Id == 0)
            throw new InvalidOperationException("The purchase was never added; add it before updating it.");

        var tracked = DbSet.Local.FirstOrDefault(e => e.Id == purchase.Id);
        if (tracked is not null)
        {
            CopyMutableFields(purchase, tracked);
            return;
        }

        // Not loaded in this context: attach the row as it is and mark only the mutable columns
        var entity = MapDomainToEntity(purchase);
        entity.Id = purchase.Id;
        DbSet.Attach(entity);
        var entry = _context.Entry(entity);
        entry.Property(e => e.Status).IsModified = true;
        entry.Property(e => e.LeaseStartHeight).IsModified = true;
        entry.Property(e => e.ClosedAtHeight).IsModified = true;
        entry.Property(e => e.ClosedEarly).IsModified = true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LiquidityPurchaseModel>> GetByChannelIdAsync(ChannelId channelId)
    {
        var entities = await DbSet.AsNoTracking()
                                  .Where(e => e.ChannelId == channelId)
                                  .OrderBy(e => e.CreatedAt)
                                  .ThenBy(e => e.Id)
                                  .ToListAsync();

        return entities.Select(MapEntityToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task<LiquidityPurchaseModel?> GetByFundingTxIdAsync(ChannelId channelId, TxId fundingTxId)
    {
        var entity = await DbSet.AsNoTracking()
                                .FirstOrDefaultAsync(e => e.ChannelId == channelId && e.FundingTxId == fundingTxId);

        return entity is null ? null : MapEntityToDomain(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LiquidityPurchaseModel>> ListAsync(LiquidityPurchaseRole? role,
                                                                       LiquidityPurchaseStatus? status, int skip,
                                                                       int take)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegative(take);
        if (take == 0)
            return [];

        var query = DbSet.AsNoTracking();
        if (role is { } r)
        {
            var roleByte = (byte)r;
            query = query.Where(e => e.Role == roleByte);
        }

        if (status is { } s)
        {
            var statusByte = (byte)s;
            query = query.Where(e => e.Status == statusByte);
        }

        var entities = await query.OrderByDescending(e => e.CreatedAt)
                                  .ThenByDescending(e => e.Id)
                                  .Skip(skip)
                                  .Take(take)
                                  .ToListAsync();

        return entities.Select(MapEntityToDomain).ToList();
    }

    /// <inheritdoc />
    public Task<int> CountPendingSalesAsync() =>
        DbSet.AsNoTracking().CountAsync(e => e.Role == SellerRole && e.Status == PendingStatus);

    /// <inheritdoc />
    public Task<int> CountPendingSalesByPeerAsync(CompactPubKey peerNodeId) =>
        DbSet.AsNoTracking()
             .CountAsync(e => e.Role == SellerRole && e.Status == PendingStatus && e.PeerNodeId == peerNodeId);

    /// <inheritdoc />
    /// <remarks>The lease end is computed in memory: a channel has a handful of purchases.</remarks>
    public async Task<LiquidityPurchaseModel?> GetActiveSaleLeaseAsync(ChannelId channelId, uint currentHeight)
    {
        var entities = await DbSet.AsNoTracking()
                                  .Where(e => e.ChannelId == channelId && e.Role == SellerRole
                                           && (e.Status == PendingStatus || e.Status == ActiveStatus))
                                  .ToListAsync();

        return entities.Select(MapEntityToDomain)
                       .Where(p => p.IsLeaseInForce(currentHeight))
                       .OrderByDescending(p => p.CreatedAt)
                       .ThenByDescending(p => p.Id)
                       .FirstOrDefault();
    }

    private void OnSavedChanges(object? sender, SavedChangesEventArgs e)
    {
        foreach (var (model, entity) in _added.Where(a => a.Entity.Id != 0).ToList())
        {
            model.AssignId(entity.Id);
            _added.RemoveAll(a => ReferenceEquals(a.Model, model));
        }
    }

    private static void CopyMutableFields(LiquidityPurchaseModel purchase, LiquidityPurchaseEntity entity)
    {
        entity.Status = (byte)purchase.Status;
        entity.LeaseStartHeight = purchase.LeaseStartHeight;
        entity.ClosedAtHeight = purchase.ClosedAtHeight;
        entity.ClosedEarly = purchase.ClosedEarly;
    }

    internal static LiquidityPurchaseEntity MapDomainToEntity(LiquidityPurchaseModel purchase)
    {
        return new LiquidityPurchaseEntity
        {
            ChannelId = purchase.ChannelId,
            FundingTxId = purchase.FundingTxId,
            Role = (byte)purchase.Role,
            Kind = (byte)purchase.Kind,
            RequestedSat = checked((long)purchase.RequestedSat),
            ContributedSat = checked((long)purchase.ContributedSat),
            RateMinAmountSat = purchase.Rate.MinAmountSat,
            RateMaxAmountSat = purchase.Rate.MaxAmountSat,
            RateFundingWeight = purchase.Rate.FundingWeight,
            RateFeeBasis = purchase.Rate.FeeBasis,
            RateFeeBaseSat = purchase.Rate.FeeBaseSat,
            RateChannelCreationFeeSat = purchase.Rate.ChannelCreationFeeSat,
            PaymentType = (byte)purchase.PaymentType,
            MiningFeeSat = checked((long)purchase.MiningFeeSat),
            ServiceFeeSat = checked((long)purchase.ServiceFeeSat),
            Signature = purchase.Signature.Value.ToArray(),
            FundingScript = purchase.FundingScript.ToArray(),
            PeerNodeId = purchase.PeerNodeId,
            LeaseBlocks = purchase.LeaseBlocks,
            Status = (byte)purchase.Status,
            LeaseStartHeight = purchase.LeaseStartHeight,
            ClosedAtHeight = purchase.ClosedAtHeight,
            ClosedEarly = purchase.ClosedEarly,
            CreatedAt = purchase.CreatedAt
        };
    }

    internal static LiquidityPurchaseModel MapEntityToDomain(LiquidityPurchaseEntity entity)
    {
        var rate = new FundingRate(entity.RateMinAmountSat, entity.RateMaxAmountSat, entity.RateFundingWeight,
                                   entity.RateFeeBasis, entity.RateFeeBaseSat, entity.RateChannelCreationFeeSat);

        return LiquidityPurchaseModel.Restore(entity.Id, entity.ChannelId, entity.FundingTxId,
                                              (LiquidityPurchaseRole)entity.Role, (LiquidityPurchaseKind)entity.Kind,
                                              checked((ulong)entity.RequestedSat),
                                              checked((ulong)entity.ContributedSat), rate,
                                              (LiquidityPaymentType)entity.PaymentType,
                                              checked((ulong)entity.MiningFeeSat),
                                              checked((ulong)entity.ServiceFeeSat),
                                              new CompactSignature(entity.Signature), entity.FundingScript,
                                              entity.PeerNodeId, entity.LeaseBlocks, entity.CreatedAt,
                                              (LiquidityPurchaseStatus)entity.Status, entity.LeaseStartHeight,
                                              entity.ClosedAtHeight, entity.ClosedEarly);
    }
}