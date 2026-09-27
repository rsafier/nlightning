using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Payment;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Offers.Enums;
using Domain.Offers.Interfaces;
using Domain.Offers.Models;
using Domain.Payments.Enums;
using Persistence.Contexts;
using Persistence.Entities.Payment;

/// <summary>
/// Stores the BOLT 12 offers we created (BOLT 12 plan §3.10, migration <c>AddBolt12Offers</c>), keyed by offer id.
/// </summary>
/// <remarks>
/// Writes are staged on the unit of work. <see cref="GetByIdAsync"/> and <see cref="GetByOfferBytesAsync"/> see what
/// this unit of work staged (they go through the change tracker); <see cref="ListAsync"/> and the invoice counts read
/// what is saved.
/// </remarks>
public class OfferDbRepository : BaseDbRepository<OfferEntity>, IOfferDbRepository
{
    private const byte Bolt12Kind = (byte)InvoiceKind.Bolt12;
    private const byte OpenStatus = (byte)InvoiceStatus.Open;
    private const byte SettledStatus = (byte)InvoiceStatus.Settled;

    private readonly NLightningDbContext _context;

    public OfferDbRepository(NLightningDbContext context) : base(context)
    {
        _context = context;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">An offer with the same id exists.</exception>
    public async Task AddAsync(OfferModel offer)
    {
        ArgumentNullException.ThrowIfNull(offer);

        if (await DbSet.FindAsync(offer.OfferId) is not null)
            throw new InvalidOperationException($"An offer with id {offer.OfferId} already exists");

        Insert(new OfferEntity
        {
            OfferId = offer.OfferId,
            Bolt12 = offer.Bolt12,
            OfferBytes = offer.OfferBytes.ToArray(),
            Description = offer.Description,
            AmountMsat = offer.Amount is null ? null : checked((long)offer.Amount.MilliSatoshi),
            Currency = offer.Currency,
            Issuer = offer.Issuer,
            QuantityMax = offer.QuantityMax,
            AbsoluteExpiry = offer.AbsoluteExpiry,
            Metadata = offer.Metadata.ToArray(),
            IssuerKind = (byte)offer.IssuerKind,
            HasPaths = offer.HasPaths,
            Status = (byte)offer.Status,
            CreatedAt = offer.CreatedAt,
            DisabledAt = offer.DisabledAt
        });
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The offer does not exist.</exception>
    public async Task UpdateAsync(OfferModel offer)
    {
        ArgumentNullException.ThrowIfNull(offer);

        var entity = await DbSet.FindAsync(offer.OfferId)
                  ?? throw new InvalidOperationException($"No offer with id {offer.OfferId}");
        entity.Status = (byte)offer.Status;
        entity.DisabledAt = offer.DisabledAt;
    }

    /// <inheritdoc />
    public async Task<OfferModel?> GetByIdAsync(Hash offerId)
    {
        var entity = await DbSet.FindAsync(offerId);
        return entity is null ? null : MapEntityToDomain(entity);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The offer id is SHA256 of the offer bytes (<see cref="OfferModel.OfferId"/>), so the lookup is by primary key;
    /// the stored bytes must still match exactly.
    /// </remarks>
    public async Task<OfferModel?> GetByOfferBytesAsync(ReadOnlyMemory<byte> offerBytes)
    {
        if (offerBytes.IsEmpty)
            return null;

        var offerId = new Hash(SHA256.HashData(offerBytes.Span));
        var entity = await DbSet.FindAsync(offerId);
        if (entity is null || !offerBytes.Span.SequenceEqual(entity.OfferBytes))
            return null;

        return MapEntityToDomain(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OfferModel>> ListAsync(bool activeOnly, int skip, int take)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegative(take);
        if (take == 0)
            return [];

        const byte active = (byte)OfferStatus.Active;
        var query = DbSet.AsNoTracking();
        if (activeOnly)
            query = query.Where(e => e.Status == active);

        var entities = await query.OrderByDescending(e => e.CreatedAt)
                                  .ThenByDescending(e => e.OfferId)
                                  .Skip(skip)
                                  .Take(take)
                                  .ToListAsync();

        return entities.Select(MapEntityToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task<OfferInvoiceCounts> GetInvoiceCountsAsync(Hash offerId, DateTimeOffset now)
    {
        Hash? id = offerId;
        var paid = await _context.Invoices.AsNoTracking()
                                 .CountAsync(e => e.Kind == Bolt12Kind && e.OfferId == id
                                                                       && e.Status == SettledStatus);
        var open = await _context.Invoices.AsNoTracking()
                                 .Where(e => e.Kind == Bolt12Kind && e.OfferId == id && e.Status == OpenStatus)
                                 .Select(e => new { e.CreatedAt, e.ExpirySeconds })
                                 .ToListAsync();

        return new OfferInvoiceCounts(paid, open.Count(e => now < e.CreatedAt.AddSeconds(e.ExpirySeconds)));
    }

    /// <inheritdoc />
    public async Task<int> CountUnpaidInvoicesAsync(DateTimeOffset now)
    {
        // The expiry is CreatedAt + ExpirySeconds, which does not translate over the ticks converter on every
        // provider; the open BOLT 12 rows are capped (plan D11), so they are filtered here
        var open = await _context.Invoices.AsNoTracking()
                                 .Where(e => e.Kind == Bolt12Kind && e.Status == OpenStatus)
                                 .Select(e => new { e.CreatedAt, e.ExpirySeconds })
                                 .ToListAsync();

        return open.Count(e => now < e.CreatedAt.AddSeconds(e.ExpirySeconds));
    }

    internal static OfferModel MapEntityToDomain(OfferEntity entity)
    {
        return new OfferModel(entity.OfferId, entity.Bolt12, entity.OfferBytes, entity.Description,
                              entity.AmountMsat is { } msat ? LightningMoney.MilliSatoshis(checked((ulong)msat)) : null,
                              entity.Currency, entity.Issuer, entity.QuantityMax, entity.AbsoluteExpiry,
                              entity.Metadata, (OfferIssuerKind)entity.IssuerKind, entity.HasPaths,
                              entity.CreatedAt, (OfferStatus)entity.Status, entity.DisabledAt);
    }
}