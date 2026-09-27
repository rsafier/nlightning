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
    private const byte AcceptedStatus = (byte)InvoiceStatus.Accepted;
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
        var rows = _context.Invoices.AsNoTracking().Where(e => e.Kind == Bolt12Kind && e.OfferId == id);
        var paid = await rows.CountAsync(e => e.Status == SettledStatus);

        return new OfferInvoiceCounts(paid, await CountUnpaidAsync(rows, now));
    }

    /// <inheritdoc />
    public Task<int> CountUnpaidInvoicesAsync(DateTimeOffset now) =>
        CountUnpaidAsync(_context.Invoices.AsNoTracking().Where(e => e.Kind == Bolt12Kind), now);

    /// <summary>
    /// Counts, in the database, the accepted rows and the open rows that have not expired at <paramref name="now"/>.
    /// </summary>
    /// <remarks>
    /// The expiry <c>CreatedAt + ExpirySeconds</c> does not translate over the ticks converter, so the open rows are
    /// counted once per distinct <c>ExpirySeconds</c> (our configured invoice expiries, a handful) with the bound
    /// <c>CreatedAt &gt; now - ExpirySeconds</c>, which is exact: <c>AddSeconds</c> of whole seconds is exact in ticks.
    /// </remarks>
    internal static async Task<int> CountUnpaidAsync(IQueryable<InvoiceEntity> bolt12Rows, DateTimeOffset now)
    {
        var unpaid = await bolt12Rows.CountAsync(e => e.Status == AcceptedStatus);

        var open = bolt12Rows.Where(e => e.Status == OpenStatus);
        var expiries = await open.Select(e => e.ExpirySeconds).Distinct().ToListAsync();
        foreach (var expiry in expiries)
        {
            // Unexpired: now < CreatedAt + expiry, that is CreatedAt > now - expiry
            var createdAfter = now.AddSeconds(-(double)expiry);
            unpaid += await open.CountAsync(e => e.ExpirySeconds == expiry && e.CreatedAt > createdAfter);
        }

        return unpaid;
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