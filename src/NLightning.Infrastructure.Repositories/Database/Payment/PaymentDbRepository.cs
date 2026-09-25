using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Payment;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Protocol.Onion.Enums;
using Persistence.Contexts;
using Persistence.Entities.Payment;

/// <summary>
/// Stores our outgoing payments (BOLT2 plan N8-T3), one row per payment hash (the latest attempt), with the route and
/// per-hop shared secrets in <c>PaymentHops</c>.
/// </summary>
/// <remarks>
/// Writes are staged on the unit of work. <see cref="GetByPaymentHashAsync"/> sees what this unit of work staged
/// (it goes through the change tracker); <see cref="GetInFlightAsync"/> and <see cref="ListAsync"/> read what is saved.
/// </remarks>
public class PaymentDbRepository : BaseDbRepository<PaymentEntity>, IPaymentDbRepository
{
    private readonly NLightningDbContext _context;

    public PaymentDbRepository(NLightningDbContext context) : base(context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public async Task AddAsync(PaymentModel payment)
    {
        ArgumentNullException.ThrowIfNull(payment);
        if (payment.Route.Count > byte.MaxValue + 1)
            throw new ArgumentException("A route has at most 256 hops", nameof(payment));

        var existing = await DbSet.FindAsync(payment.PaymentHash);
        if (existing is null)
        {
            var entity = new PaymentEntity
            {
                PaymentHash = payment.PaymentHash,
                PayeeNodeId = payment.PayeeNodeId,
                AmountMsat = 0,
                FeeMsat = 0,
                CreatedAt = payment.CreatedAt,
                Status = 0
            };
            MapDomainToEntity(payment, entity);
            Insert(entity);
            SyncHops(payment, entity.PaymentHash, []);
            return;
        }

        if (existing.Status != (byte)PaymentStatus.Failed)
            throw new InvalidOperationException(
                $"A payment for payment hash {payment.PaymentHash} is {(PaymentStatus)existing.Status}; only a failed "
              + "payment can be replaced");

        // A retry of a failed payment replaces the stored attempt, route included
        MapDomainToEntity(payment, existing);
        existing.PayeeNodeId = payment.PayeeNodeId;
        existing.CreatedAt = payment.CreatedAt;
        SyncHops(payment, existing.PaymentHash, await LoadTrackedHopsAsync(payment.PaymentHash));
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The payment does not exist.</exception>
    public async Task UpdateAsync(PaymentModel payment)
    {
        ArgumentNullException.ThrowIfNull(payment);

        var entity = await DbSet.FindAsync(payment.PaymentHash)
                  ?? throw new InvalidOperationException($"No payment for payment hash {payment.PaymentHash}");
        MapMutableFields(payment, entity);
    }

    /// <inheritdoc />
    public async Task<PaymentModel?> GetByPaymentHashAsync(Hash paymentHash)
    {
        var entity = await DbSet.FindAsync(paymentHash);
        if (entity is null)
            return null;

        return MapEntityToDomain(entity, await LoadTrackedHopsAsync(paymentHash));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PaymentModel>> GetInFlightAsync()
    {
        const byte inFlight = (byte)PaymentStatus.InFlight;
        var entities = await DbSet.AsNoTracking()
                                  .Include(e => e.Hops)
                                  .Where(e => e.Status == inFlight)
                                  .OrderBy(e => e.CreatedAt)
                                  .ToListAsync();

        return entities.Select(e => MapEntityToDomain(e, e.Hops ?? [])).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PaymentModel>> ListAsync(int skip, int take)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegative(take);
        if (take == 0)
            return [];

        var entities = await DbSet.AsNoTracking()
                                  .Include(e => e.Hops)
                                  .OrderByDescending(e => e.CreatedAt)
                                  .ThenByDescending(e => e.PaymentHash)
                                  .Skip(skip)
                                  .Take(take)
                                  .ToListAsync();

        return entities.Select(e => MapEntityToDomain(e, e.Hops ?? [])).ToList();
    }

    internal static PaymentModel MapEntityToDomain(PaymentEntity entity, IEnumerable<PaymentHopEntity> hops)
    {
        var route = hops.OrderBy(h => h.HopIndex)
                        .Select(h => new PaymentHop(h.NodeId, h.ShortChannelId,
                                                    LightningMoney.MilliSatoshis(checked((ulong)h.AmountMsat)),
                                                    h.CltvExpiry, new Secret(h.SharedSecret)))
                        .ToList();

        return PaymentModel.Restore(entity.PaymentHash, entity.Bolt11, entity.PayeeNodeId,
                                    LightningMoney.MilliSatoshis(checked((ulong)entity.AmountMsat)),
                                    LightningMoney.MilliSatoshis(checked((ulong)entity.FeeMsat)), entity.CreatedAt,
                                    (PaymentStatus)entity.Status, entity.OutgoingChannelId, entity.OutgoingHtlcId,
                                    entity.Preimage is { } preimage ? new Secret(preimage) : (Secret?)null,
                                    entity.FailureCode is { } code ? (FailureCode)code : (FailureCode?)null,
                                    entity.FailureSourceIndex, entity.FailureReason, entity.CompletedAt, route);
    }

    private static void MapDomainToEntity(PaymentModel payment, PaymentEntity entity)
    {
        entity.Bolt11 = payment.Bolt11;
        entity.AmountMsat = checked((long)payment.Amount.MilliSatoshi);
        entity.FeeMsat = checked((long)payment.Fee.MilliSatoshi);
        MapMutableFields(payment, entity);
    }

    private static void MapMutableFields(PaymentModel payment, PaymentEntity entity)
    {
        entity.Status = (byte)payment.Status;
        entity.OutgoingChannelId = payment.OutgoingChannelId;
        entity.OutgoingHtlcId = payment.OutgoingHtlcId;
        entity.Preimage = payment.Preimage is { } preimage ? ((byte[])preimage).ToArray() : null;
        entity.FailureCode = payment.FailureCode is { } code ? (ushort)code : null;
        entity.FailureSourceIndex = payment.FailureSourceIndex;
        entity.FailureReason = payment.FailureReason;
        entity.CompletedAt = payment.CompletedAt;
    }

    /// <summary>
    /// The hop rows of a payment as the change tracker sees them: the stored ones (tracked) plus the ones this unit of
    /// work added, without the ones it deleted.
    /// </summary>
    private async Task<List<PaymentHopEntity>> LoadTrackedHopsAsync(Hash paymentHash)
    {
        var hops = await _context.PaymentHops.Where(h => h.PaymentHash == paymentHash).ToListAsync();
        hops.AddRange(_context.PaymentHops.Local.Where(h => h.PaymentHash == paymentHash && !hops.Contains(h)));

        return hops.Where(h => _context.Entry(h).State != EntityState.Deleted).ToList();
    }

    /// <summary>
    /// Makes the hop rows of <paramref name="paymentHash"/> equal to <paramref name="payment"/>'s route, row by row by
    /// hop index (an index that is kept is updated in place, never deleted and re-added under the same key).
    /// </summary>
    private void SyncHops(PaymentModel payment, Hash paymentHash, List<PaymentHopEntity> existing)
    {
        var byIndex = existing.ToDictionary(h => h.HopIndex);
        for (var i = 0; i < payment.Route.Count; i++)
        {
            var hop = payment.Route[i];
            var index = (byte)i;
            if (!byIndex.Remove(index, out var entity))
            {
                entity = new PaymentHopEntity
                {
                    PaymentHash = paymentHash,
                    HopIndex = index,
                    NodeId = hop.NodeId,
                    ShortChannelId = hop.ShortChannelId,
                    AmountMsat = 0,
                    CltvExpiry = 0,
                    SharedSecret = []
                };
                _context.PaymentHops.Add(entity);
            }

            entity.NodeId = hop.NodeId;
            entity.ShortChannelId = hop.ShortChannelId;
            entity.AmountMsat = checked((long)hop.Amount.MilliSatoshi);
            entity.CltvExpiry = hop.CltvExpiry;
            entity.SharedSecret = ((byte[])hop.SharedSecret).ToArray();
        }

        foreach (var stale in byIndex.Values)
            _context.PaymentHops.Remove(stale);
    }
}