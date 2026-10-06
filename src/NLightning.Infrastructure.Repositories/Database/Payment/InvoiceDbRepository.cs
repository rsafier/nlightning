using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Payment;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Offers.Models;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Keysend;
using Domain.Payments.Models;
using Persistence.Contexts;
using Persistence.Entities.Payment;

/// <summary>
/// Stores the invoices we issued (BOLT2 plan N8-T2), keyed by payment hash: BOLT 11 invoices and, since migration
/// <c>AddBolt12Offers</c>, the BOLT 12 invoices of our offers (<see cref="InvoiceModel.Bolt12"/>, written by
/// <see cref="AddAsync"/> only).
/// </summary>
/// <remarks>
/// Writes are staged on the unit of work. <see cref="GetByPaymentHashAsync"/> sees what this unit of work staged
/// (it goes through the change tracker); <see cref="ListAsync"/> reads what is saved.
/// <para>Keysend records (<c>InvoiceKind.Keysend</c> = 2): no BOLT 11 string and no BOLT 12 details, and the
/// <c>CustomRecords</c> column holds the payer's custom records as a TLV stream (<see cref="CustomRecordCodec"/>;
/// empty when there are none; migration <c>AddPaymentCustomRecords</c>).</para>
/// </remarks>
public class InvoiceDbRepository : BaseDbRepository<InvoiceEntity>, IInvoiceDbRepository
{
    private const byte Bolt12Kind = (byte)InvoiceKind.Bolt12;
    private const byte OpenStatus = (byte)InvoiceStatus.Open;
    private const byte SettledStatus = (byte)InvoiceStatus.Settled;

    public InvoiceDbRepository(NLightningDbContext context) : base(context)
    {
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">An invoice with the same payment hash exists.</exception>
    public async Task AddAsync(InvoiceModel invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        if (await DbSet.FindAsync(invoice.PaymentHash) is not null)
            throw new InvalidOperationException($"An invoice for payment hash {invoice.PaymentHash} already exists");

        Insert(new InvoiceEntity
        {
            PaymentHash = invoice.PaymentHash,
            Preimage = invoice.Preimage is { } preimage ? ((byte[])preimage).ToArray() : null,
            PaymentSecret = ((byte[])invoice.PaymentSecret).ToArray(),
            AmountMsat = ToMsat(invoice.Amount),
            Description = invoice.Description,
            Bolt11 = invoice.Bolt11,
            Kind = (byte)invoice.Kind,
            OfferId = invoice.Bolt12?.OfferId,
            Bolt12InvoiceBytes = invoice.Bolt12?.InvoiceBytes.ToArray(),
            CustomRecords = invoice.Keysend is { } keysend
                                ? CustomRecordCodec.Encode(keysend.CustomRecords)
                                : null,
            InvoiceRequestPayerId = invoice.Bolt12?.PayerId,
            Quantity = invoice.Bolt12?.Quantity,
            PayerNote = invoice.Bolt12?.PayerNote,
            CreatedAt = invoice.CreatedAt,
            ExpirySeconds = invoice.ExpirySeconds,
            MinFinalCltvExpiry = invoice.MinFinalCltvExpiry,
            Status = (byte)invoice.Status,
            AmountReceivedMsat = ToMsat(invoice.AmountReceived),
            SettledAt = invoice.SettledAt,
            Label = invoice.Label,
            Tags = invoice.Tags,
            Htlcs = InvoiceHtlcCodec.Encode(invoice.Htlcs)
        });
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The invoice does not exist.</exception>
    public async Task UpdateAsync(InvoiceModel invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        var entity = await DbSet.FindAsync(invoice.PaymentHash)
                  ?? throw new InvalidOperationException($"No invoice for payment hash {invoice.PaymentHash}");
        entity.Status = (byte)invoice.Status;
        entity.AmountReceivedMsat = ToMsat(invoice.AmountReceived);
        entity.SettledAt = invoice.SettledAt;
        // A hold invoice (NL-995) gains its preimage with the operator's settle
        entity.Preimage = invoice.Preimage is { } preimage ? ((byte[])preimage).ToArray() : entity.Preimage;
        // The paying HTLCs once held or settled (NL-1167)
        entity.Htlcs = InvoiceHtlcCodec.Encode(invoice.Htlcs) ?? entity.Htlcs;
    }

    /// <inheritdoc />
    public async Task<InvoiceModel?> GetByPaymentHashAsync(Hash paymentHash)
    {
        var entity = await DbSet.FindAsync(paymentHash);
        return entity is null ? null : MapEntityToDomain(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InvoiceModel>> ListAsync(int skip, int take)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegative(take);
        if (take == 0)
            return [];

        var entities = await DbSet.AsNoTracking()
                                  .OrderByDescending(e => e.CreatedAt)
                                  .ThenByDescending(e => e.PaymentHash)
                                  .Skip(skip)
                                  .Take(take)
                                  .ToListAsync();

        return entities.Select(MapEntityToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InvoiceModel>> ListByIndexAsync(LndIndexQuery query, bool openOnly)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Take <= 0)
            return [];

        var set = DbSet.AsNoTracking().Where(e => e.AddIndex != null);
        if (query.After is { } after)
        {
            var bound = ToIndex(after);
            set = set.Where(e => e.AddIndex > bound);
        }

        if (query.Before is { } before)
        {
            var bound = ToIndex(before);
            set = set.Where(e => e.AddIndex < bound);
        }

        if (query.CreatedFrom is { } from)
            set = set.Where(e => e.CreatedAt >= from);
        if (query.CreatedUntil is { } until)
            set = set.Where(e => e.CreatedAt <= until);
        if (openOnly)
            set = set.Where(e => e.Status == OpenStatus);
        set = query.Ascending ? set.OrderBy(e => e.AddIndex) : set.OrderByDescending(e => e.AddIndex);
        var entities = await set.Take(query.Take).ToListAsync();
        return entities.Select(MapEntityToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InvoiceModel>> ListSettledAfterAsync(ulong settleIndexAfter, int take)
    {
        if (take <= 0)
            return [];

        var bound = ToIndex(settleIndexAfter);
        var entities = await DbSet.AsNoTracking()
                                  .Where(e => e.SettleIndex != null && e.SettleIndex > bound)
                                  .OrderBy(e => e.SettleIndex)
                                  .Take(take)
                                  .ToListAsync();
        return entities.Select(MapEntityToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task<(ulong AddIndex, ulong SettleIndex)> GetMaxIndexesAsync()
    {
        var add = await DbSet.AsNoTracking().MaxAsync(e => e.AddIndex) ?? 0;
        var settle = await DbSet.AsNoTracking().MaxAsync(e => e.SettleIndex) ?? 0;
        return ((ulong)add, (ulong)settle);
    }

    /// <summary>An LND index as the stored long (the largest index any store reaches is far below 2^63).</summary>
    private static long ToIndex(ulong index) => (long)Math.Min(index, long.MaxValue);

    /// <inheritdoc />
    public async Task<IReadOnlyList<InvoiceModel>> ListSettledAsync(DateTimeOffset settledAtOrBefore, int skip,
                                                                    int take)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegative(take);
        if (take == 0)
            return [];

        var entities = await DbSet.AsNoTracking()
                                  .Where(e => e.Status == SettledStatus && e.SettledAt != null
                                           && e.SettledAt <= settledAtOrBefore)
                                  .OrderBy(e => e.SettledAt)
                                  .ThenBy(e => e.PaymentHash)
                                  .Skip(skip)
                                  .Take(take)
                                  .ToListAsync();

        return entities.Select(MapEntityToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InvoiceModel>> ListSettledByOfferIdAsync(Hash offerId)
    {
        var entities = await DbSet.AsNoTracking()
                                  .Where(e => e.OfferId == offerId && e.Status == SettledStatus)
                                  .OrderBy(e => e.SettledAt)
                                  .ThenBy(e => e.PaymentHash)
                                  .ToListAsync();

        return entities.Select(MapEntityToDomain).ToList();
    }

    /// <inheritdoc />
    /// <remarks>
    /// The expiry <c>CreatedAt + ExpirySeconds</c> does not translate over the ticks converter, so the expired rows are
    /// selected once per distinct <c>ExpirySeconds</c> (our configured invoice expiries, a handful) with the bound
    /// <c>CreatedAt &lt;= now - ExpirySeconds</c>, at most <paramref name="max"/> each, oldest first. A row this unit of
    /// work already moved out of <c>Open</c> is skipped.
    /// </remarks>
    public async Task<int> PruneExpiredBolt12InvoicesAsync(DateTimeOffset now, int max)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(max);
        if (max == 0)
            return 0;

        var open = DbSet.Where(e => e.Kind == Bolt12Kind && e.Status == OpenStatus);
        var expiries = await open.Select(e => e.ExpirySeconds).Distinct().ToListAsync();
        var expired = new List<InvoiceEntity>();
        foreach (var expiry in expiries)
        {
            // Expired: now >= CreatedAt + expiry, that is CreatedAt <= now - expiry
            var createdAtOrBefore = now.AddSeconds(-(double)expiry);
            expired.AddRange(await open.Where(e => e.ExpirySeconds == expiry && e.CreatedAt <= createdAtOrBefore)
                                       .OrderBy(e => e.CreatedAt)
                                       .Take(max)
                                       .ToListAsync());
        }

        var pruned = expired.Where(e => e.Status == OpenStatus)
                            .OrderBy(e => e.CreatedAt.AddSeconds(e.ExpirySeconds))
                            .Take(max)
                            .ToList();
        DbSet.RemoveRange(pruned);

        return pruned.Count;
    }

    internal static InvoiceModel MapEntityToDomain(InvoiceEntity entity)
    {
        return new InvoiceModel(entity.PaymentHash,
                                entity.Preimage is { } preimage ? new Secret(preimage) : (Secret?)null,
                                new Secret(entity.PaymentSecret),
                                ToMoney(entity.AmountMsat), entity.Description, entity.Bolt11, entity.CreatedAt,
                                entity.ExpirySeconds, entity.MinFinalCltvExpiry, (InvoiceStatus)entity.Status,
                                ToMoney(entity.AmountReceivedMsat), entity.SettledAt, MapBolt12(entity),
                                MapKeysend(entity))
        {
            Label = entity.Label,
            Tags = entity.Tags,
            AddIndex = entity.AddIndex is { } addIndex ? (ulong)addIndex : null,
            SettleIndex = entity.SettleIndex is { } settleIndex ? (ulong)settleIndex : null,
            Htlcs = InvoiceHtlcCodec.Decode(entity.Htlcs)
        };
    }

    private static KeysendDetails? MapKeysend(InvoiceEntity entity)
    {
        if (entity.Kind != (byte)InvoiceKind.Keysend)
            return null;

        // Unreadable bytes give a record without custom records, never an exception under the switch's hash lock
        CustomRecordCodec.TryDecode(entity.CustomRecords ?? [], out var records);
        return new KeysendDetails(records);
    }

    private static Bolt12InvoiceDetails? MapBolt12(InvoiceEntity entity)
    {
        if (entity.Kind != (byte)InvoiceKind.Bolt12)
            return null;

        if (entity.OfferId is not { } offerId || entity.Bolt12InvoiceBytes is null
                                              || entity.InvoiceRequestPayerId is not { } payerId)
            throw new InvalidOperationException(
                $"The BOLT 12 invoice for payment hash {entity.PaymentHash} lacks its offer, bytes or payer id");

        return new Bolt12InvoiceDetails(offerId, entity.Bolt12InvoiceBytes, payerId, entity.Quantity,
                                        entity.PayerNote);
    }

    private static long? ToMsat(LightningMoney? amount) =>
        amount is null ? null : checked((long)amount.MilliSatoshi);

    private static LightningMoney? ToMoney(long? msat) =>
        msat is { } value ? LightningMoney.MilliSatoshis(checked((ulong)value)) : null;
}