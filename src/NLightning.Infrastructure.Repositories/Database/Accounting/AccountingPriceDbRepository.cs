using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Accounting;

using Domain.Accounting.Constants;
using Domain.Accounting.Financial;
using Persistence.Contexts;
using Persistence.Entities.Accounting;

/// <summary>
/// The stored prices (<c>AccountingPrices</c>, NL-602 A3-T2; migration <c>AddAccountingFinancial</c>). Writes are
/// staged on the unit of work and committed by its save.
/// </summary>
public class AccountingPriceDbRepository : IAccountingPriceDbRepository
{
    private readonly NLightningDbContext _context;

    public AccountingPriceDbRepository(NLightningDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <inheritdoc />
    public async Task<bool> TryAddAsync(AccountingPrice price, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(price);
        var currency = NormalizeCurrency(price.Currency);

        if (_context.AccountingPrices.Local.Any(p => p.Currency == currency && p.Time == price.Time)
         || await _context.AccountingPrices.AsNoTracking()
                          .AnyAsync(p => p.Currency == currency && p.Time == price.Time, cancellationToken))
            return false;

        _context.AccountingPrices.Add(new AccountingPriceEntity
        {
            Currency = currency,
            Time = price.Time,
            Price = price.Price,
            Source = (byte)price.Source,
            FetchedAt = price.FetchedAt
        });
        return true;
    }

    /// <inheritdoc />
    public async Task<AccountingPrice?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        var entity = await _context.AccountingPrices.AsNoTracking()
                                   .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        return entity is null ? null : MapEntityToDomain(entity);
    }

    /// <inheritdoc />
    public async Task<AccountingPrice?> GetAtOrBeforeAsync(string currency, DateTimeOffset time, TimeSpan maxAge,
                                                           CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAge, TimeSpan.Zero);
        var code = NormalizeCurrency(currency);
        var oldest = time - maxAge;

        var entity = await _context.AccountingPrices.AsNoTracking()
                                   .Where(p => p.Currency == code && p.Time <= time && p.Time >= oldest)
                                   .OrderByDescending(p => p.Time)
                                   .FirstOrDefaultAsync(cancellationToken);
        return entity is null ? null : MapEntityToDomain(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountingPrice>> ListAsync(string currency, DateTimeOffset? since,
                                                                DateTimeOffset? until, int take,
                                                                CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        var code = NormalizeCurrency(currency);

        var prices = _context.AccountingPrices.AsNoTracking().Where(p => p.Currency == code);
        if (since is { } from)
            prices = prices.Where(p => p.Time >= from);
        if (until is { } to)
            prices = prices.Where(p => p.Time < to);

        var entities = await prices.OrderBy(p => p.Time).Take(take).ToListAsync(cancellationToken);
        return entities.Select(MapEntityToDomain).ToList();
    }

    private static string NormalizeCurrency(string currency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        var code = currency.Trim().ToUpperInvariant();
        if (code.Length != AccountingSchemaLimits.CurrencyLength || !code.All(char.IsAsciiLetterUpper))
            throw new ArgumentException($"'{currency}' is not an ISO 4217 code", nameof(currency));

        return code;
    }

    private static AccountingPrice MapEntityToDomain(AccountingPriceEntity entity) =>
        new(entity.Id, entity.Currency, entity.Time, entity.Price, (AccountingPriceSource)entity.Source,
            entity.FetchedAt);
}