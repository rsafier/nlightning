using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Payment;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Persistence.Contexts;
using Persistence.Entities.Payment;

/// <summary>
/// Stores the parts (offered HTLCs) of our outgoing payments with their routes (NL-321), one row per offered part,
/// keyed by (payment hash, part index), so a part that is not the one the <c>Payments</c> row records can still be
/// resolved and its error decrypted after a restart.
/// </summary>
/// <remarks>
/// Writes are staged on the unit of work. Rows are read by their key prefix (the payment hash) and by the offered
/// (channel, HTLC id); the per-part routes come back with their hops and shared secrets.
/// </remarks>
public class PaymentPartDbRepository(NLightningDbContext context)
    : BaseDbRepository<PaymentPartEntity>(context), IPaymentPartDbRepository
{
    private readonly NLightningDbContext _context = context;

    /// <inheritdoc />
    public async Task AddAsync(PaymentPartModel part)
    {
        ArgumentNullException.ThrowIfNull(part);

        var entity = new PaymentPartEntity
        {
            PaymentHash = part.PaymentHash,
            PartIndex = part.PartIndex,
            ChannelId = part.ChannelId,
            HtlcId = part.HtlcId,
            State = (byte)part.State
        };
        Insert(entity);
        SyncHops(part, entity);
    }

    /// <inheritdoc />
    public async Task UpdateAsync(PaymentPartModel part)
    {
        ArgumentNullException.ThrowIfNull(part);

        // A tracking query: it sees what this unit of work staged (through the change tracker) and loads the hop rows
        // the update syncs
        var entity = await DbSet.Include(e => e.Hops)
                                .FirstOrDefaultAsync(e => e.PaymentHash == part.PaymentHash
                                                       && e.PartIndex == part.PartIndex)
                    ?? throw new InvalidOperationException(
                           $"No part {part.PartIndex} of payment {part.PaymentHash} is stored.");

        entity.State = (byte)part.State;
        SyncHops(part, entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PaymentPartModel>> GetForPaymentAsync(Hash paymentHash)
    {
        var entities = await DbSet.AsNoTracking()
                                  .Include(e => e.Hops)
                                  .Where(e => e.PaymentHash == paymentHash)
                                  .OrderBy(e => e.PartIndex)
                                  .ToListAsync();

        return entities.Select(MapEntityToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task<PaymentPartModel?> GetByHtlcAsync(Hash paymentHash, ChannelId channelId, ulong htlcId)
    {
        var entity = await DbSet.AsNoTracking()
                                .Include(e => e.Hops)
                                .SingleOrDefaultAsync(e => e.PaymentHash == paymentHash
                                                        && e.ChannelId == channelId && e.HtlcId == htlcId);
        return entity is null ? null : MapEntityToDomain(entity);
    }

    /// <inheritdoc />
    public async Task DeleteForPaymentAsync(Hash paymentHash)
    {
        // Staged: the rows reach the database with the unit of work's save, and the hops' foreign key cascades there
        DeleteRange(await DbSet.Where(e => e.PaymentHash == paymentHash).ToListAsync());
    }

    internal static PaymentPartModel MapEntityToDomain(PaymentPartEntity entity)
    {
        var hops = entity.Hops?.OrderBy(h => h.HopIndex)
                              .Select(h => new PaymentHop(h.NodeId, h.ShortChannelId,
                                                          LightningMoney.MilliSatoshis(checked((ulong)h.AmountMsat)),
                                                          h.CltvExpiry, new Secret(h.SharedSecret),
                                                          h.HoldTimeMs is { } ms
                                                              ? TimeSpan.FromMilliseconds(ms)
                                                              : null))
                              .ToList() ?? [];

        return new PaymentPartModel(entity.PaymentHash, entity.PartIndex, entity.ChannelId, entity.HtlcId,
                                    (PaymentPartState)entity.State, hops);
    }

    /// <summary>
    /// Makes the hop rows of a part equal to the part's route, row by row by hop index.
    /// </summary>
    private void SyncHops(PaymentPartModel part, PaymentPartEntity entity)
    {
        var existing = (entity.Hops ??= []).OrderBy(h => h.HopIndex).ToList();
        for (var i = 0; i < part.Hops.Count; i++)
        {
            var hop = part.Hops[i];
            var index = (byte)i;
            var row = existing.FirstOrDefault(h => h.HopIndex == index);
            if (row is null)
            {
                row = new PaymentPartHopEntity
                {
                    PaymentHash = part.PaymentHash,
                    PartIndex = part.PartIndex,
                    HopIndex = index,
                    NodeId = hop.NodeId,
                    ShortChannelId = hop.ShortChannelId,
                    AmountMsat = 0,
                    CltvExpiry = 0,
                    SharedSecret = []
                };
                _context.PaymentPartHops.Add(row);
                entity.Hops.Add(row);
            }

            row.NodeId = hop.NodeId;
            row.ShortChannelId = hop.ShortChannelId;
            row.AmountMsat = checked((long)hop.Amount.MilliSatoshi);
            row.CltvExpiry = hop.CltvExpiry;
            row.SharedSecret = ((byte[])hop.SharedSecret).ToArray();
            row.HoldTimeMs = hop.HoldTime is { } holdTime ? (long)holdTime.TotalMilliseconds : null;
        }

        foreach (var stale in existing.Where(h => h.HopIndex >= part.Hops.Count))
        {
            entity.Hops.Remove(stale);
            _context.PaymentPartHops.Remove(stale);
        }
    }
}