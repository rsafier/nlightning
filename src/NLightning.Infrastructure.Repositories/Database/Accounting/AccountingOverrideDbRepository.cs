using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Accounting;

using Domain.Accounting.Financial;
using Persistence.Contexts;
using Persistence.Entities.Accounting;

/// <summary>
/// The manual reclassifications (<c>AccountingOverrides</c>, NL-602 A3-T3; migration <c>AddAccountingFinancial</c>),
/// one per event key. Writes are staged on the unit of work and committed by its save.
/// </summary>
public class AccountingOverrideDbRepository : IAccountingOverrideDbRepository
{
    private readonly NLightningDbContext _context;

    public AccountingOverrideDbRepository(NLightningDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <inheritdoc />
    public async Task SetAsync(AccountingOverride accountingOverride, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accountingOverride);
        ArgumentException.ThrowIfNullOrEmpty(accountingOverride.EventKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountingOverride.Account);

        var entity = await FindTrackedAsync(accountingOverride.EventKey, cancellationToken);
        if (entity is null)
        {
            _context.AccountingOverrides.Add(new AccountingOverrideEntity
            {
                EventKey = accountingOverride.EventKey,
                Account = accountingOverride.Account,
                Note = accountingOverride.Note,
                CreatedAt = accountingOverride.CreatedAt
            });
            return;
        }

        entity.Account = accountingOverride.Account;
        entity.Note = accountingOverride.Note;
        entity.CreatedAt = accountingOverride.CreatedAt;
    }

    /// <inheritdoc />
    public async Task<AccountingOverride?> GetAsync(string eventKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventKey);

        var staged = _context.AccountingOverrides.Local.FirstOrDefault(o => o.EventKey == eventKey);
        if (staged is not null)
            return MapEntityToDomain(staged);

        var entity = await _context.AccountingOverrides.AsNoTracking()
                                   .FirstOrDefaultAsync(o => o.EventKey == eventKey, cancellationToken);
        return entity is null ? null : MapEntityToDomain(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, AccountingOverride>> GetManyAsync(
        IReadOnlyCollection<string> eventKeys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventKeys);
        if (eventKeys.Count == 0)
            return new Dictionary<string, AccountingOverride>();

        var keys = eventKeys.Distinct().ToList();
        var entities = await _context.AccountingOverrides.AsNoTracking()
                                     .Where(o => keys.Contains(o.EventKey))
                                     .ToListAsync(cancellationToken);
        return entities.ToDictionary(e => e.EventKey, MapEntityToDomain);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountingOverride>> ListAsync(int skip, int take,
                                                                   CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegative(take);
        if (take == 0)
            return [];

        var entities = await _context.AccountingOverrides.AsNoTracking()
                                     .OrderBy(o => o.EventKey)
                                     .Skip(skip)
                                     .Take(take)
                                     .ToListAsync(cancellationToken);
        return entities.Select(MapEntityToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(string eventKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventKey);

        var entity = await FindTrackedAsync(eventKey, cancellationToken);
        if (entity is null)
            return false;

        _context.AccountingOverrides.Remove(entity);
        return true;
    }

    private async Task<AccountingOverrideEntity?> FindTrackedAsync(string eventKey,
                                                                   CancellationToken cancellationToken) =>
        _context.AccountingOverrides.Local.FirstOrDefault(o => o.EventKey == eventKey)
     ?? await _context.AccountingOverrides.FirstOrDefaultAsync(o => o.EventKey == eventKey, cancellationToken);

    private static AccountingOverride MapEntityToDomain(AccountingOverrideEntity entity) =>
        new(entity.EventKey, entity.Account, entity.Note, entity.CreatedAt);
}