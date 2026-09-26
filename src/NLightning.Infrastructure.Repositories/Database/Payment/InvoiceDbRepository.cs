using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Payment;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Persistence.Contexts;
using Persistence.Entities.Payment;

/// <summary>
/// Stores the invoices we issued (BOLT2 plan N8-T2), keyed by payment hash.
/// </summary>
/// <remarks>
/// Writes are staged on the unit of work. <see cref="GetByPaymentHashAsync"/> sees what this unit of work staged
/// (it goes through the change tracker); <see cref="ListAsync"/> reads what is saved.
/// </remarks>
public class InvoiceDbRepository : BaseDbRepository<InvoiceEntity>, IInvoiceDbRepository
{
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
            Preimage = ((byte[])invoice.Preimage).ToArray(),
            PaymentSecret = ((byte[])invoice.PaymentSecret).ToArray(),
            AmountMsat = ToMsat(invoice.Amount),
            Description = invoice.Description,
            Bolt11 = invoice.Bolt11,
            CreatedAt = invoice.CreatedAt,
            ExpirySeconds = invoice.ExpirySeconds,
            MinFinalCltvExpiry = invoice.MinFinalCltvExpiry,
            Status = (byte)invoice.Status,
            AmountReceivedMsat = ToMsat(invoice.AmountReceived),
            SettledAt = invoice.SettledAt
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

    internal static InvoiceModel MapEntityToDomain(InvoiceEntity entity)
    {
        return new InvoiceModel(entity.PaymentHash, new Secret(entity.Preimage), new Secret(entity.PaymentSecret),
                                ToMoney(entity.AmountMsat), entity.Description, entity.Bolt11, entity.CreatedAt,
                                entity.ExpirySeconds, entity.MinFinalCltvExpiry, (InvoiceStatus)entity.Status,
                                ToMoney(entity.AmountReceivedMsat), entity.SettledAt);
    }

    private static long? ToMsat(LightningMoney? amount) =>
        amount is null ? null : checked((long)amount.MilliSatoshi);

    private static LightningMoney? ToMoney(long? msat) =>
        msat is { } value ? LightningMoney.MilliSatoshis(checked((ulong)value)) : null;
}